﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Net;
using System.Net.Sockets;

namespace Client_ViggiCoin_v2._0
{
    class CServer
    {
        //RSA
        //TODO: cambiare l'inizializzazione una volta definite le classi
        public static RSAKey PublicKey = null;
        public static RSAKey PrivateKey = null;

        private Thread mUpdateBlockChainThread;
        private CPeers mPeers;
        private static int MAX_PEERS = 30;//deve essere pari
        private static int RESERVED_CONNECTION = MAX_PEERS / 2;//connessioni usate per chi vuole collegarsi con me
        private static int NOT_RESERVED_CONNECTION = MAX_PEERS - RESERVED_CONNECTION;//connessioni che utilizzo io per collegarmi agli altri

        private Thread mThreadListener, mThreadPeers;
        private Socket mListener;
        private static int DEFOULT_PORT = 100;

        private ulong mLastBlockNumber;

        private bool IsStopped = false; //set true per spegnere il server

        private CServer(List<CPeer> Peers)
        {
            RSA.GenRSAKey(PublicKey,PrivateKey);
            mLastBlockNumber = CBlockChain.Instance.LastBlock.BlockNumber;
            if (Program.DEBUG)
                CIO.DebugOut("Last block number: " + mLastBlockNumber+".");

            if (Program.DEBUG)
                CIO.DebugOut("Inizialize mPeers...");
            mPeers = new CPeers(MAX_PEERS, RESERVED_CONNECTION);

            if (Program.DEBUG)
                CIO.DebugOut("Inizialie the Listener...");
            //crea un socket che attende connessioni in ingresso di peer che vogliono collegarsi, in ascolto sulla porta DEFOULT_PORT
            IPEndPoint localEndPoint = new IPEndPoint(IPAddress.Any, DEFOULT_PORT);
            mListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            mListener.Bind(localEndPoint);
            mListener.Listen(DEFOULT_PORT);

            if (Program.DEBUG)
                CIO.DebugOut("Finish inizializing!");
            Start(Peers);
        }

        public static CServer StartNewServer(List<CPeer> Peers)
        {
            if (Peers?.Count > 0)
                return new CServer(Peers);
            return null;
        }

        private int ConnectedPeers
        {
            get { return mPeers.NumConnection(); }
        }

        private void Start(List<CPeer> Peers)
        {
            if (Program.DEBUG)
                CIO.DebugOut("Begin to enstablish connections to initial peers...");
            //si collega ai peer inseriti nella lista iniziale.
            foreach (CPeer p in Peers)
                if (p.Connect())
                    if (!mPeers.Insert(p))
                        break;

            if (Program.DEBUG)
                CIO.DebugOut("Begin to enstablish connections to other peers...");
            mThreadPeers = new Thread(new ThreadStart(UpdatePeersList));
            mThreadPeers.Start();
            
            if (Program.DEBUG)
                CIO.DebugOut("Start listening...");
            mThreadListener = new Thread(new ThreadStart(StartAcceptUsersConnection));
            mThreadListener.Start();
            
            if (Program.DEBUG)
                CIO.DebugOut("Start update blockchain...");
            mUpdateBlockChainThread = new Thread(new ThreadStart(UpdateBlockchain));
            mUpdateBlockChainThread.Start();
            
        }

        private void UpdatePeersList()
        {
            while (!IsStopped)
            {
                if (mPeers.NumConnection() < NOT_RESERVED_CONNECTION)
                    mPeers.DoRequest(ERequest.UpdatePeers);
                Thread.Sleep(10000);
            }
        }


        //attende il collegamento di nuovi peer
        private void StartAcceptUsersConnection()
        {
            //crea un eventargs per una richiesta di connessione asincrona, se la lista dei peers non è ancora piena inizia ad attendere fino a quando non riceve
            //una richiesta di connessione o il segnale d'arresto. Se viene ricevuta una richiesta di connessione viene chiamata la funzione InsertNewPeer che
            //inserisce il nuovo peer nella lista dei peer mPeers
            
            //è asincrono perchè altrimenti al segnale di spegnimento non si fermerebbe  
            SocketAsyncEventArgs asyncConnection;
            bool IncomingConnection = false;
            if (Program.DEBUG)
                Console.WriteLine("Attending connection...");
            while (!IsStopped)
            {
                if (ConnectedPeers < MAX_PEERS)
                {
                    IncomingConnection = false;
                    asyncConnection = new SocketAsyncEventArgs();
                    asyncConnection.Completed += (object sender, SocketAsyncEventArgs e) => { IncomingConnection = true; };
                    mListener.AcceptAsync(asyncConnection);
                    while (!IncomingConnection && !IsStopped)
                    {
                        Thread.Sleep(1000);
                    }
                    if (IncomingConnection)
                    {
                        if (Program.DEBUG)
                            CIO.DebugOut("Established connection!");
                        InsertNewPeer(asyncConnection.AcceptSocket);
                    }
                    asyncConnection.Dispose();

                }
                else
                {
                    Thread.Sleep(10000);
                }
            }
            //TODO
            //CloseAllConnection();
            if (Program.DEBUG)
                CIO.WriteLine("Chiuse tutte le connessioni con gli users");
        }

        private void UpdateBlockchain()
        {
            //TODO download a blocchi invece che copleto in una volta.
            while (!IsStopped)
            {
                CBlock lastBlc = new CBlock();
                CBlock[] newBlock;
                bool[] blockchainValidity=new bool[]{false};

                mPeers.DoRequest(ERequest.CheckBlockchainValidity,blockchainValidity);
                if (blockchainValidity[0])
                {
                    mPeers.DoRequest(ERequest.LastBlockNumber, lastBlc);
                    if (lastBlc.BlockNumber > CBlockChain.Instance.LastBlock.BlockNumber)
                    {
                        newBlock = new CBlock[lastBlc.BlockNumber - CBlockChain.Instance.LastBlock.BlockNumber];
                        //(!) i vari peer potrebbero avere versioni diverse della blockchain, è un problema? forse dovrei scaricare solo da chi ha il blocco più vecchio
                        // ^  Già risolto validando un blocco alla volta? fai dei test
                        mPeers.DoRequest(ERequest.DownloadMissingBlock, newBlock);
                        foreach (CBlock b in newBlock)
                            if (CBlockChain.Validate(b))
                                CBlockChain.Add(b);
                            else
                                break;
                        //TODO se c'è un blocco non valido fare qualcosa, per esempio controllare il blocco non valido tra i vari peer e poi se viene trovato un blocco valido ripartire da quello
                    }

                }
                else
                {

                }
                    Thread.Sleep(10000);
            
            }
        }

        private void InsertNewPeer(Socket NewConnection)
        {
            //crea un nuovo peer con un socket già collegato e una nuova connessione con questo peer, e la inserisce nel contenitore mConnections
            CPeer newPeer = CPeer.CreatePeer(Convert.ToString((NewConnection.RemoteEndPoint as IPEndPoint).Address), (NewConnection.RemoteEndPoint as IPEndPoint).Port, NewConnection);
            mPeers.Insert(newPeer, true);
        }

        static public byte[] ReceiveData(Socket Receiving)
        {
            byte[] data = new byte[4];
            Receiving.Receive(data);
            data = new byte[BitConverter.ToInt32(data, 0)];
            Receiving.Receive(data);
            return data;
        }

        static public void SendData(Socket Dispatcher, byte[] data)
        {
            Dispatcher.Send(BitConverter.GetBytes(data.Length));
            Dispatcher.Send(data);
        }
    }
}

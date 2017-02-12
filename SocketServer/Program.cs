/*
 * DWEYW LICENSE
 * This file is provided purely for educational purposes. The author (Stefan Andersson) claim no copyright or 
 * license to this file.
 * You are free to do whatever you want with this file, including removing this notice, as long as you 
 * understand and agree the author will take no responsibility
 * whatsoever regarding functionality or fitness of purpose.
 * 
 * Happy hacking!
 * Stefan Andersson
*/

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SocketServer
{

    class Program
    {
        static void Main(string[] args)
        {
            var listener = SocketListener.Create(1234, (message, session) =>
            {
                if (message == ClientRequestString)
                {
                    session.Write(ServerResponseString);
                }
            });

            listener.Start();

            var t = new Timer(s =>
            {
                ConnectAsTcpClient();
            }, null, 2000, 2000);

            Console.WriteLine("Keyboard waiting for newline");
            Console.ReadLine();

            listener.Stop();
        }

        private static void ConnectAsTcpClient()
        {
            using (var tcpClient = new TcpClient())
            {
                Console.WriteLine("[Client] Connecting to server");
                var clientConnect = tcpClient.ConnectAsync("127.0.0.1", 1234);

                clientConnect.Wait(1000);

                Console.WriteLine("[Client] Connected to server");
                using (var ns = tcpClient.GetStream())
                {
                    var sw = new StreamWriter(ns)
                    {
                        AutoFlush = true
                    };

                    Console.WriteLine("[Client] Writing request {0}", ClientRequestString);
                    var clientWriteTask = sw.WriteLineAsync(ClientRequestString);
                    clientWriteTask.Wait(100);

                    var sr = new StreamReader(ns);

                    var responseReadTask = sr.ReadLineAsync();

                    responseReadTask.Wait(500);
                    var response = responseReadTask.Result;

                    Console.WriteLine("[Client] Server response was {0}", response);
                }
            }
        }

        private static readonly string ClientRequestString = "Some CR terminated request here";

        private static readonly string ServerResponseString = "<?xml version=\"1.0\" encoding=\"utf-8\"?><document><userkey>key</userkey> <machinemode>1</machinemode><serial>0000</serial><unitname>Device</unitname><version>1</version></document>";

        class SocketListener
        {
            private readonly Action<string, JsonSession> onMessage;
            private readonly TcpListener tcpListener;
            private CancellationTokenSource socketCancellation;

            private SocketListener(Action<string, JsonSession> onMessage, TcpListener tcpListener)
            {
                this.onMessage = onMessage;
                this.tcpListener = tcpListener;
                this.socketCancellation = new CancellationTokenSource();
            }

            public static SocketListener Create(int port, Action<string, JsonSession> onMessage)
            {
                var tcpListener = TcpListener.Create(port);
                tcpListener.Start();

                return new SocketListener(onMessage, tcpListener);
            }

            public void Stop()
            {
                this.socketCancellation.Cancel();
                this.tcpListener.Stop();
            }

            public void Start()
            {
                Task.Run(() =>
                {
                    while (!this.socketCancellation.IsCancellationRequested)
                    {
                        var acceptTcpClienTask = tcpListener.AcceptTcpClientAsync();
                        acceptTcpClienTask.Wait(this.socketCancellation.Token);

                        Console.WriteLine("[Server] Client has connected");

                        var tcpClient = acceptTcpClienTask.Result;
                        var session = new JsonSession(tcpClient.GetStream(), onMessage);

                        try
                        {
                            session.Start();
                        }
                        catch (Exception ex)
                        {
                            if (ex is OperationCanceledException)
                            {
                                Console.WriteLine("Timed out listening for message. Opening up for accepting a new one.");
                            }
                            else
                            {
                                Console.WriteLine("Unhandled exception " + ex.Message);
                            }
                        }
                    }
                });
            }
        }

        class JsonSession
        {
            readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();

            //  private readonly TcpClient tcpClient;

            private CancellationTokenSource readCancellation;
            private CancellationTokenSource writeCancellation;

            private NetworkStream networkStream;
            private readonly Action<string, JsonSession> onMessage;
            private StreamWriter streamWriter;

            public JsonSession(NetworkStream networkStream, Action<string, JsonSession> onMessage)
            {
                this.networkStream = networkStream;
                this.onMessage = onMessage;
            }

            /// <summary>
            /// Sends a message back to the socket client. This can be done by several threads, so it needs to be thread-safe
            ///  </summary>
            /// <param name="message"></param>
            public void Write(string message)
            {
                // -- enqueue the message on a concurrentqueue, which is thread-safe
                messages.Enqueue(message);

                // -- another thread might have emptied the queue once we get here, 
                //    if so we don't need to spawn another one
                if (messages.Count > 0)
                {
                    Task.Run(() => WriteAll());
                }
            }

            private void WriteAll()
            {
                // -- messages need to be sequentialized, so halt any other threads attempting to write

                try
                {
                    string writeMessage;
                    while (messages.TryDequeue(out writeMessage))
                    {

                        Console.WriteLine("[Client] Writing request {0}", ClientRequestString);

                        lock (streamWriter)
                        {
                            var writeTask = streamWriter.WriteLineAsync(writeMessage);

                            // -- try writing the message, timeout after 1 second
                            writeTask.Wait(writeCancellation.Token);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Write encountered exception " + e.Message + " - " + (messages.Count + 1) + " messages lost.");
                }
            }

            public void Start()
            {
                readCancellation = new CancellationTokenSource();
                readCancellation.CancelAfter(TimeSpan.FromSeconds(5));

                writeCancellation = new CancellationTokenSource();
                writeCancellation.CancelAfter(TimeSpan.FromSeconds(1));

                streamWriter = new StreamWriter(networkStream)
                {
                    AutoFlush = true
                };

                var sr = new StreamReader(networkStream);

                // -- Listen for reads until CancellationTokenException is thrown, which will unwind all the way back to the client socket
                // -- which means we should drop the connection and wait for a new one.

                while (true)
                {
                    // -- for as long as we get anything within 5s, just keep going

                    // -- We need to wrap ReadLineAsync in an outer Task because it does not expose 
                    //    the option to wait for a cancellation token (as ReadAsync does)

                    var readTask = Task.Run(sr.ReadLineAsync, readCancellation.Token);
                    readTask.Wait(readCancellation.Token);

                    var request = readTask.Result;

                    // -- now we have the result, pass it on to the handler
                    onMessage(request, this);

                }
            }
        }
    }
}
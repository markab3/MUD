using System;
using System.Net;
using MUD.Telnet;
using MongoDB.Bson;
using MongoDB.Driver;
using MUD.Core;
using System.Linq;
using System.Threading;
using MUD.Core.Formatting;
using MongoDB.Bson.Serialization;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson.Serialization.IdGenerators;

namespace MUD.Main
{
    class Program
    {
        private static World _gameWorld;
        private static Server _serverInstance;
        private static string welcomeMessage =
@"
                     Welcome to Planar Realms 0.1
                     _____  _                         
                    |  __ \| |                        
                    | |__) | | __ _ _ __   __ _ _ __  
                    |  ___/| |/ _` | '_ \ / _` | '__| 
                    | |    | | (_| | | | | (_| | |    
               />___|_|___ |_|\__,_|_|_|_|\__,_|_|__________    
     [#########[]____________________________________________\
               \>   |  __ \          | |              
                    | |__) |___  __ _| |_ __ ___  ___ 
                    |  _  // _ \/ _` | | '_ ` _ \/ __|
                    | | \ \  __/ (_| | | | | | | \__ \
                    |_|  \_\___|\__,_|_|_| |_| |_|___/
                                                                        
--------------------------------------------------------------------------------
    Use 'connect <username> <password>' to connect to an existing player.
    Use 'create <username> <password>' to create a new player.
    Use 'who' to see who is currently online.
    Use 'quit' to disconnect.
--------------------------------------------------------------------------------";

        static void Main(string ip = null, int port = 23, string db = null)
        {
            Console.Write("Checking databases...");
            MongoClient dbClient = null;

            try
            {
                dbClient = new MongoClient(db);
                //setup our DI
                IServiceProvider _serviceProvider = new ServiceCollection()
                    .AddSingleton<IMongoClient>(dbClient)
                    .AddTransient<Item>()
                    .AddTransient<Wand>()
                    .AddTransient<Food>()
                    .BuildServiceProvider();

                // Apparently SetIsRootClass can't be set after the ClassMap has been Frozen.
                // BsonClassMap.RegisterClassMap<Item>(cm =>
                // {
                //     cm.AutoMap();
                //     cm.SetIsRootClass(true);
                //     cm.MapIdProperty("Id").SetIdGenerator(StringObjectIdGenerator.Instance);
                //     cm.SetCreator(() => { return _serviceProvider.GetService<Item>(); });
                // });
                // BsonClassMap.RegisterClassMap<Wand>(cm =>
                // {
                //     cm.AutoMap();
                //     cm.SetCreator(() => { return _serviceProvider.GetService<Wand>(); });
                // });
                // BsonClassMap.RegisterClassMap<Food>(cm =>
                // {
                //     cm.AutoMap();
                //     cm.SetCreator(() => { return _serviceProvider.GetService<Food>(); });
                // });

                var database = dbClient.GetDatabase("testmud"); // This creates the database if it doesn't otherwise exist?
                if (database == null) { Console.WriteLine("Database not found!"); }
                var collection = database.GetCollection<BsonDocument>("players"); // Check that the collection is there. If it is not, then the collection exists in memory and is created on the actual DB when needed.
                var testFind = collection.AsQueryable().FirstOrDefault();
                // collection = database.GetCollection<BsonDocument>("rooms"); // Check that the collection is there.
                // collection.InsertOne(BsonDocument.Parse(jsonContent)); // The BSON object will have _id and it will be set by the insert call.

                var helper = new Data.MongoDBClassMapManager(_serviceProvider);
                helper.ClearClassMaps();
                helper.RegisterRootClassMap(typeof(Item));
                helper.RegisterClassMap(typeof(Wand));
                helper.RegisterClassMap(typeof(Food));

                var itemCollection = database.GetCollection<Item>("testingstuff");

                itemCollection.DeleteMany((m => true));

                Item newItem = new Item(dbClient)
                {
                    ShortDescription = "soft cloth",
                    LongDescription = "This is a small, soft cloth. It is suitable for polishing things or blowing one's nose, but not much else."
                };
                newItem.Update();

                Wand newWand = new Wand(dbClient)
                {
                    ShortDescription = "wand of missiles",
                    LongDescription = "This is a short, straight stick that has been polished and painted red. It has been imbued with magical power such that it will shoot magic missiles at one's foe.",
                    CurrentCharges = 8,
                    MaxCharges = 10
                };
                newWand.Update();

                Food newFood = new Food(dbClient)
                {
                    ShortDescription = "loaf of bread",
                    LongDescription = "A toasty, scrumptious loaf of fresh bread.",
                    Servings = 2
                };
                newFood.Update();

                var item = itemCollection.Find<Item>((m => m.Id == newItem.Id)).FirstOrDefault();
                var wand = (Wand)itemCollection.Find<Item>((m => m.Id == newWand.Id)).FirstOrDefault();
                wand.UseWand();
                wand.Update();
                var food = itemCollection.Find<Item>((m => m.Id == newFood.Id)).FirstOrDefault();

                // Can it be done with a type we don't know til runtime?
                //BsonClassMap.RegisterClassMap(new BsonClassMap(typeof(Item))); // Yes, but not with the initializer?

                // Can I then look up the class map and change it? Yes. 
                //var classMap = BsonClassMap.LookupClassMap(typeof(Item)); // This also registers the type and automaps.
                //classMap.SetCreator(() => { return _serviceProvider.GetService(typeof(Item)); });

                // Try this dirty dirty reflection stuff I found? Yeah that worked.
                helper.ClearClassMaps();

                BsonClassMap.RegisterClassMap<Item>(cm =>
               {
                   cm.SetIsRootClass(true);
                   cm.AutoMap();
                   cm.MapIdProperty("Id").SetIdGenerator(StringObjectIdGenerator.Instance);
                   cm.SetCreator(() => { return _serviceProvider.GetService<Item>(); });
               });
                BsonClassMap.RegisterClassMap<Wand>(cm =>
                {
                    cm.AutoMap();
                    cm.SetCreator(() => { return _serviceProvider.GetService<Wand>(); });
                });
                BsonClassMap.RegisterClassMap<Food>(cm =>
                {
                    cm.AutoMap();
                    cm.SetCreator(() => { return _serviceProvider.GetService<Food>(); });
                });

            }
            catch (Exception ex)
            {
                Console.WriteLine("Could not connect to suitable database!");
                Console.WriteLine(ex);
                return;
            }
            Console.WriteLine("done.");


            Console.Write("Starting world...");
            _gameWorld = new World(dbClient);
            Thread gameWorldThread = new Thread(_gameWorld.Start);
            gameWorldThread.Start();
            Console.WriteLine("done.");

            Console.Write("Starting telnet server...");
            IPAddress ipAddress = null;
            if (!IPAddress.TryParse(ip, out ipAddress)) { ipAddress = IPAddress.Any; }

            _serverInstance = new Server(ipAddress, port);
            _serverInstance.ClientConnected += clientConnected;
            _serverInstance.ClientDisconnected += clientDisconnected;

            Thread serverThread = new Thread(_serverInstance.Start);
            serverThread.Start();
            Console.WriteLine("done.");

            Console.WriteLine("SERVER STARTED: " + DateTime.Now);

            char read2 = ' ';
            bool isInteractive = true;

            while (read2 != 'q')
            {
                if (read2 == 'b')
                {
                    _serverInstance.SendMessageToAll(Console.ReadLine());
                }

                if (read2 == 'c')
                {
                    string colorTest = "";
                    for (int i = 0; i < 256; i++)
                    {
                        colorTest += "\x1B[38;5;" + i + "m " + i.ToString("D3") + " \x1B[0m";
                        if (i == 15 || i == 231 || ((i > 15) && (i < 232) && (((i - 15) % 6) == 0)))
                        {
                            colorTest += "\r\n";
                        }
                    }
                    _serverInstance.SendMessageToAll(colorTest);

                    _serverInstance.SendMessageToAll("\x1B[32mGREEN\x1B[0m");
                    _serverInstance.SendMessageToAll("\x1B[92mBRIGHT GREEN\x1B[0m");
                    _serverInstance.SendMessageToAll("\x1B[1m\x1B[32mBOLD GREEN\x1B[0m");
                    _serverInstance.SendMessageToAll("\x1B[1m\x1B[92mBOLD BRIGHT GREEN\x1B[0m");
                    _serverInstance.SendMessageToAll("\x1B[42mGREEN BACKGROUND\x1B[0m");
                    _serverInstance.SendMessageToAll("\x1B[102mBRIGHT GREEN BACKGROUND\x1B[0m");
                    _serverInstance.SendMessageToAll("\x1B[1m\x1B[42mBOLD GREEN BACKGROUND\x1B[0m");
                    _serverInstance.SendMessageToAll("\x1B[1m\x1B[102mBOLD BRIGHT GREEN BACKGROUND\x1B[0m");
                }

                if (read2 == 't')
                {
                    _serverInstance.SendMessageToAll(new byte[] { (byte)CommandCode.IAC, (byte)CommandCode.DO, (byte)OptionCode.TerminalType });
                }

                if (isInteractive)
                {
                    try
                    {
                        read2 = Console.ReadKey(true).KeyChar;
                    }
                    catch (InvalidOperationException)
                    {
                        // Can't accept console input.
                        isInteractive = false;
                    }
                }
            }

            _gameWorld.Stop();
            _serverInstance.Stop();
        }

        private static void clientConnected(object sender, Client client)
        {
            Console.WriteLine("CONNECTED: " + client);

            client.DataReceived += messageReceived;
            client.Send(welcomeMessage);
            messageReceived(client, "who");
            client.Send("");
        }

        private static void clientDisconnected(object sender, Client client)
        {
            client.DataReceived -= messageReceived;
            Console.WriteLine("DISCONNECTED: " + client);
        }

        private static void connectionBlocked(IPEndPoint ep)
        {
            Console.WriteLine(string.Format("BLOCKED: {0}:{1} at {2}", ep.Address, ep.Port, DateTime.Now));
        }

        private static void messageReceived(object sender, string message)
        {
            Client client = (Client)sender;
            message = message.TrimEnd('\n').TrimEnd('\r');

            Console.WriteLine("MESSAGE: " + message);

            if (message == "quit" || message.StartsWith("quit "))
            {
                client.Disconnect();
            }
            else if (message == "who" || message.StartsWith("who "))
            {
                // Show the list of logged in players
                if (_gameWorld.Players == null || _gameWorld.Players.Count == 0)
                {
                    client.Send("No one is logged on.");
                }
                else if (_gameWorld.Players.Count == 1)
                {
                    client.Send(String.Format("{0} is logged on.", _gameWorld.Players.First().PlayerName));
                }
                else
                {
                    client.Send(String.Format("{0} are logged on.", _gameWorld.Players.Select(p => p.PlayerName).GetListText()));
                }
            }
            else if (message == "connect" || message.StartsWith("connect "))
            {
                string[] args = message.Substring(8).Split(' ');

                if (args.Length != 2 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1]))
                {
                    // bad input. 
                    client.Send("Please provide a username and password to connect. Format: connect <username> <password>");
                    return;
                }

                string username = args[0];
                string password = args[1];

                // begin login attempt.
                if (_gameWorld.DoLogin(username, password, client))
                {
                    client.DataReceived -= messageReceived;
                }
                else
                {
                    // Player not found.
                    client.Send(String.Format("Login failed for {0}.", username));
                    return;
                }
            }
            else if (message == "create" || message.StartsWith("create "))
            {
                string[] args = message.Substring(7).Split(' ');

                if (args.Length != 2 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1]))
                {
                    // bad input. 
                    client.Send("Please provide a username and password to create a new account. Format: create <username> <password>");
                    return;
                }

                string username = args[0];
                string password = args[1];

                // begin character creation attempt.
                if (_gameWorld.DoCreateUser(username, password, client))
                {
                    client.DataReceived -= messageReceived;
                }
                else
                {
                    // Player not found.
                    client.Send(String.Format("There is already someone by the name of {0}.", username));
                    return;
                }
            }
            else
            {
                client.Send(string.Format("Command {0} was not recognized.", message.Substring(0, message.IndexOf(" "))));
            }
        }
    }
}
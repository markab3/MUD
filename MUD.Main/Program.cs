using System;
using System.Net;
using MUD.Telnet;
using MongoDB.Bson;
using MongoDB.Driver;
using MUD.Core;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Scrutor;
using MUD.Core.Commands;
using MUD.Core.Repositories.Interfaces;
using MUD.Core.Repositories;
using Microsoft.Extensions.Configuration;
using System.IO;
using MUD.Core.Entities;

namespace MUD.Main
{
    class Program
    {
        private static IServiceProvider _serviceProvider;
        private static World _gameWorld;
        private static Server _serverInstance;
        private static IConfigurationRoot configuration;
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

        static void Main(string[] args)
        {
            // Build configuration
            configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetParent(AppContext.BaseDirectory).FullName)
                .AddJsonFile("appsettings.json", false)
                .Build();

            Console.Write("Checking databases...");
            MongoClient dbClient = new MongoClient(configuration.GetConnectionString("MongoDBConnection"));
            var database = dbClient.GetDatabase("testmud"); // This creates the database if it doesn't otherwise exist?
            if (database == null) { Console.WriteLine("Database not found!"); }
            var collection = database.GetCollection<BsonDocument>("players"); // Check that the collection is there. If it is not, then the collection exists in memory and is created on the actual DB when needed.
            if (collection == null) { database.CreateCollection("players"); }
            collection = database.GetCollection<BsonDocument>("rooms"); // Check that the collection is there.
            //collection.InsertOne(BsonDocument.Parse(jsonContent)); // The BSON object will have _id and it will be set by the insert call.
            Console.WriteLine("done.");


            //setup our DI
            _serviceProvider = new ServiceCollection()
                // Load all commands 
                .Scan(scan => scan
                    //.FromCallingAssembly()
                    //                    .FromAssemblies(new System.Reflection.Assembly[] {})
                    .FromApplicationDependencies()
                    .AddClasses(classes => classes.AssignableTo<ICommand>())
                    .AsImplementedInterfaces()
                    .WithSingletonLifetime()
                )
                .AddSingleton<IMongoClient>(dbClient)
                .AddSingleton<IPlayerRepository, PlayerRepository>()
                .AddSingleton<IRoomRepository, RoomRepository>()
                .BuildServiceProvider();

            System.Collections.Generic.List<ICommand> loadedCommands = _serviceProvider.GetServices<ICommand>().ToList();

            Console.Write("Starting world...");
            World.ServiceProvider = _serviceProvider;
            _gameWorld = World.Instance;
            Thread gameWorldThread = new Thread(_gameWorld.Start);
            gameWorldThread.Start();
            Console.WriteLine("done.");

            Console.Write("Starting telnet server...");
            IPAddress ipAddress = IPAddress.Any;
            int port = 23;
            if (!string.IsNullOrWhiteSpace(configuration?.GetSection("TelnetSettings")?.GetSection("IPAddress")?.Value))
            {
                ipAddress = IPAddress.Parse(configuration.GetSection("TelnetSettings").GetSection("IPAddress").Value);
            }
            if (!int.TryParse(configuration.GetSection("TelnetSettings").GetSection("Port").Value, out port)) { port = 23; }

            _serverInstance = new Server(IPAddress.Any, port);
            _serverInstance.ClientConnected += clientConnected;
            _serverInstance.ClientDisconnected += clientDisconnected;

            Thread serverThread = new Thread(_serverInstance.Start);
            serverThread.Start();
            Console.WriteLine("done.");

            Console.WriteLine("SERVER STARTED: " + DateTime.Now);
            char read2 = Console.ReadKey(true).KeyChar;

            do
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
            } while ((read2 = Console.ReadKey(true).KeyChar) != 'q');

            _gameWorld.Stop();
            _serverInstance.Stop();

            //return;

            //Player testPlayer = new Player() { PlayerName = "holen", Password = "password", Gender = "male", Race = "hobbit", Class = "rogue" };
            //var jsonContent = JsonConvert.SerializeObject(testPlayer);
        }

        private static void clientConnected(object sender, Client c)
        {
            Console.WriteLine("CONNECTED: " + c);

            c.DataReceived += messageReceived;
            c.Send(welcomeMessage);
        }

        private static void clientDisconnected(object sender, Client c)
        {
            c.DataReceived -= messageReceived;
            Console.WriteLine("DISCONNECTED: " + c);
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

            if (message == "quit")
            {
                client.Disconnect();
            }
            else if (message == "who")
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
                    client.Send(String.Format("{0} are logged on.", String.Join(", ", _gameWorld.Players.Select(p => p.PlayerName))));
                }
            }
            else if (message.StartsWith("connect "))
            {
                string[] args = message.Substring(8).Split(' ');

                if (args.Length != 2)
                {
                    // bad input. 
                    client.Send("Please provide a username and password to connect. Format: connect <username> <password>");
                    return;
                }

                string username = args[0];
                string password = args[1];

                // begin login attempt.
                var foundPlayer = doLogin(username, password, client);

                if (foundPlayer == null)
                {
                    // Player not found.
                    client.Send(String.Format("Login failed for {0}.", username));
                    return;
                }
                else
                {
                    // Ok login
                    if (_gameWorld.Players.Contains(foundPlayer))
                    {
                        // Already logged on.
                    }
                    else
                    {
                        client.DataReceived -= messageReceived;
                        _gameWorld.AddPlayer(foundPlayer);
                    }
                }
            }
            else if (message.StartsWith("create "))
            {
                string[] args = message.Substring(7).Split(' ');

                if (args.Length != 2)
                {
                    // bad input. 
                    client.Send("Please provide a username and password to create a new account. Format: create <username> <password>");
                }

                string username = args[0];
                string password = args[1];

                // begin character creation attempt.
                var newPlayer = doCreateUser(username, password, client);

                if (newPlayer == null)
                {
                    // Player not found.
                    client.Send(String.Format("There is already someone by the name of {0}.", username));
                    return;
                }
                else
                {
                    client.DataReceived -= messageReceived;
                    _gameWorld.AddPlayer(newPlayer);
                }
            }
            else
            {
                client.Send(string.Format("Command {0} was not recognized.", message.Substring(0, message.IndexOf(" "))));
            }
        }

        private static Player doLogin(string userName, string password, Client connectingClient)
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password)) { return null; }

            var playerRepository = _serviceProvider.GetService<IPlayerRepository>();
            var foundUser = playerRepository.Search(p => p.PlayerName.ToLower().Contains(userName)).FirstOrDefault();

            if (foundUser != null)
            {
                if (foundUser.Password == password)
                {
                    Player foundPlayer = new Player(playerRepository, foundUser);
                    foundPlayer.ConnectionStatus = EPlayerConnectionStatus.LoggedIn;
                    foundPlayer.Connection = connectingClient;
                    return foundPlayer;
                }
            }
            return null;
        }

        private static Player doCreateUser(string userName, string password, Client connectingClient)
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password)) { return null; }

            var playerRepository = _serviceProvider.GetService<IPlayerRepository>();
            var foundUser = playerRepository.Search(p => p.PlayerName.ToLower().Contains(userName)).FirstOrDefault();

            if (foundUser == null)
            {
                Player newPlayer = new Player(playerRepository, new PlayerEntity())
                {
                    PlayerName = userName,
                    Password = password,
                    Race = "human",
                    CurrentLocation_id = "5f6e27f20c1fdd24b4b18b1a",
                    SelectedTerm = "Default", // Or just do the subnegotiation to get a value for this...
                    ConnectionStatus = EPlayerConnectionStatus.LoggedIn,
                    Connection = connectingClient
                };
                return newPlayer;
            }
            return null;

        }
    }
}
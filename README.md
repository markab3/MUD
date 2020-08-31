# MUD
## Background/Status
This is a first crack/experimental mud server. I started with the telnet stuff, which turned out to have a few twists and turns. Now I'm wresting with getting the projects all structured right and some of the basics built out.
## Code Objectives/Ideas
**MongoDB BackEnd**
Make use of modern db software to make the back end of the MUD more flexible and reliable. Mongo chosen due to the changing nature of MUD code. Being able to edit code while you play is kinda a major component (IMO)
Things to do:
* Build out a data access layer project
* Determine best practices for serializing/deserializing and handling _id in mongo world
* Figure out streamlined way to persist objects with arbitrary classes - include class as a string in the BSON document? Make a different table for each?
**Telnet Stuff**
You can connect now, but I am pretty sure not all the edge cases are getting handled or handled correctly.
* Clean up code
* Debug dropped connections/weird cases
* Test multiple connections/concurrency stuff (incoming data is handled on the server?)
**SignalR?**
Maybe instead of (or along with) telnet, a signalR implementation could be set up so that people could play in a browser over a more modern connection?
**Runtime Dependency Injection Of Creator Code**
* Want to be able to add c# files to custom modules, compile that code, and load it in without rebooting the server.
* Should be possible to dynamically load additional projects/DLLs if they are added to dependency injection registry...
**Webservice Interconnect**
* Would be neat if you were able to set up a separate server with separate content/worlds
    * Can't work out how to pass player data from one MUD instance to another and deal with code incompatibility or not allow someone to create a MUD that would let them just stupidly powerlevel or something
    * Maybe host a web service that serves up core MUD class information in some form? Maybe just return version information...
    * Allow others to host web services and register as a "plane" that the mud server would reach out to for rooms and stuff?
**Mud Basics**
MUDs need to do some basic stuff...
* Parse commands, handle available commands
    * Some commands are givens (ex. look, search), players have commands they have learned, and objects or rooms can provide commands (use wands, interact with room items)
    * These need to handle parsing - probably regex match first word + syntax stuffs?
    * MUDs seem to usually have a command queue of some sort.. should look into that..
* Provide methods for sending content to players
    * I think most LPMud setups have functions like TellPlayer or TellRoom to send text to the appropriate clients
* Provide methods for matching input to items in the game
    * Need to be able to match swords to the broadsword, longsword, and katana, but not the dagger or the orc.
    * There's actually a lot of muckity muck string stuff to do for pluralizing things and stuff
* Create a game loop to run in the background and update things
* Rooms, Items, etc.
## Game Mechanics Ideas ##
* Visibility/Spatial system - Traditional "rooms" work pretty well for dungeons/indoor stuff don't make as much sense when talking about wide open spaces. 
    * Maybe add the ability to see things in adjacent "rooms" to simulate this. Example: "Off in the distance to the east there appear to be about three people"
        * Could add some mechanics for extending line of sight or adding detail - spyglass or scrying magic could turn that message into "Off in the distance to the east you see two orcish soldiers and an orcish captain"
        * Adds another level of "hiding" - you could be concealed to stuff outside your room, but not hidden inside.
        * Could make a grove of trees in an otherwise open plain or an elevated position actually impactful.
    * Perhaps some accounting for the fact that it takes time to trek across the wilderness other than just the speed the server can handle the commands? Movement points? Would only really be a thing "outside"
    * Handle indoor stuff as "building" objects that rooms contain - Have seen this done to a limited extent on some MUD before - it was pretty cool.
    * Might enable some form of ranged attacks?
* Dynamic room descriptions - I've seen MUDs use dynamic room descriptions in the sense that there is a big glob of rooms that have descriptions that use some randomly picked phrases as they are all essentially the same. It saves you a lot of description writing time and lets you expand the world massively, but makes some of it kinda boring or repetitive. The alternate approach is either to write unique descriptions for all of those rooms or to mention areas as "backdrops" that the players can't actually explore (Ex. "Vast expanses of grassland stretch out on either side of the road." but no exits other than to follow the road.). Both kinda suck.
    * Most MUDs have rooms that include items that can be examined in them. These are items that are mentioned in the long description of a room, but are not noteworthy enough to actually be added as "furniture" (like a wardrobe)
    * Why not make the long description dynamically build from these features? Then you can compose a lot of rooms quickly, with full examinable features and reusable components.
    * Would enable some amount of player-created room building, because it ensures that some standards are followed and constrains the types of things that could be included. Example: A player could not create a room description that mentions a "a million giant dragons slumber in the corner" - unless they could actually get a million "giant sleeping dragon" room feature items and add them to the room.
* Skills
    * Re-think some skills...
    * Perhaps instead of an overmax type system, related skills can give you a bonus? Really good with staves? That would make you better at handling a spear (but not a dagger).



using Engine.Utils;
using System.Reflection;

namespace MinimalGame;

internal class Program
{
    static void Main(string[] args)
    {
        GameStartup.Run<GameEngine>(Assembly.GetExecutingAssembly(), args);
    }
}

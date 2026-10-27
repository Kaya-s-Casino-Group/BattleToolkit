using System;
using System.IO;
using System.Linq;
using System.Text;
using PokerEngine;

internal static class BundleBridge
{
    internal static int Run(string[] args)
    {
        try
        {
            if (args.Length != 2 && args.Length != 4 || args.Length == 4 && args[2] != "--export" && args[2] != "--start")
                throw new ArgumentException("Usage: --bundle entry.xml [--export output.xml | --start encounter-id]");
            var source = BattleContentLoader.LoadFile(args[1]);
            var build = source.Success ? BattleContentCompiler.Compile(source.Source) : null;
            foreach (var diagnostic in build?.Diagnostics ?? source.Diagnostics)
                Console.Error.WriteLine(diagnostic.Location.SourceName + ":" + diagnostic.Location.Line + ":" + diagnostic.Location.Column +
                    " " + diagnostic.Severity + " " + diagnostic.Code + " " + diagnostic.Message);
            if (build == null || !build.Success) return 1;
            Console.WriteLine("Valid bundle: " + build.Content.Id + " | " + build.Content.Fingerprint);
            Console.WriteLine("Encounters: " + string.Join(", ", build.Content.Encounters.Keys));
            if (args.Length == 4 && args[2] == "--export")
            {
                File.WriteAllText(args[3], BattleContentLoader.ExportXml(source.Source), new UTF8Encoding(false));
                Console.WriteLine("Exported: " + Path.GetFullPath(args[3]));
            }
            if (args.Length == 4 && args[2] == "--start")
            {
                var assembled = BattleContentFactory.Create(build.Content, new BattleSetupRequest(args[3], "bundle-smoke-test", HmacDeckShuffler.FromSeed(new byte[32])));
                if (!assembled.Success) throw new ArgumentException(string.Join("; ", assembled.Diagnostics.Select(d => d.Message)));
                var session = assembled.Session;
                var actor = session.State.Duel.Players.First().Id;
                var start = session.Execute(session.State.Revision, actor, CardDuelAction.Start);
                if (!start.Accepted) throw new ArgumentException("Cannot start encounter: " + start.Error);
                Console.WriteLine("Started " + args[3] + ": hand=" + session.State.Zone(CardZoneKinds.Hand, actor).Cards.Count +
                    ", enemies=" + session.State.Duel.Enemies.Count + ", phase=" + session.State.Duel.Phase);
            }
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException || exception is IOException || exception is UnauthorizedAccessException)
        { Console.Error.WriteLine(exception.Message); return 1; }
    }
}

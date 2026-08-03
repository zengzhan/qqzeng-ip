using System;
using System.IO;

namespace Qqzeng
{
    class BatchProgram
    {
        static void Main(string[] args)
        {
            if (args.Length < 1) return;
            var searcher = new QzdbSearcher();
            searcher.Load(args[0]);

            string line;
            while ((line = Console.ReadLine()) != null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                var res = searcher.FindStr(line);
                Console.WriteLine(res ?? "");
            }
        }
    }
}

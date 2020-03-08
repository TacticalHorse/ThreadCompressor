using System;
using System.Diagnostics;
using System.IO.Compression;
using System.Threading.Tasks;

namespace ThreadCompressor
{
    class Program
    {
        static int Main(string[] args)
        {
            Stopwatch s = Stopwatch.StartNew();
            GzWorker a = null;
            Console.Clear();
            string res = "";
            if (args.Length == 3)
            {
                if (args[0].ToLower() == "compress" || args[0].ToLower() == "decompress")
                {
                    a = new GzWorker(args[1], args[2]);
                    res = a.Start(args[0].ToLower() == "compress" ? CompressionMode.Compress : CompressionMode.Decompress);
                }
                else
                {
                    res = "Неверно задан аргумент определяющий метод обработки файла, укажите compress или decompress.";
                }
            }
            else res = "Неверно заданы аргументы.";
            s.Stop();
            Console.WriteLine(s.Elapsed);
            Console.WriteLine(string.IsNullOrEmpty(res) ? "OK" : res);
            return string.IsNullOrEmpty(res) ? 0 : 1;

            //Task.Factory.StartNew(() => Tools.GetHash("d.d"));
            //Task.Factory.StartNew(() => Tools.GetHash("d.e"));
            //Console.Read();
            //return 0;


            //Tools.CreateFile((long)32 * 1024 * 1024 * 1024, "d.d");
            //return 0;
        }
    }
}

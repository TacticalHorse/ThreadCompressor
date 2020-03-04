using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ThreadCompressor
{
    class Program
    {
        static int Main(string[] args)
        {
            Stopwatch SW = Stopwatch.StartNew();
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
            Console.WriteLine(string.IsNullOrEmpty(res) ? "OK" : res);


            SW.Stop();
            Console.WriteLine(SW.Elapsed);

            Console.Read();
            return string.IsNullOrEmpty(res) ? 0 : 1;

            //Random rnd = new Random();
            //byte[] datafragment = new byte[32 * 1024 * 1024];
            //using (var sw = File.Create("data.dat"))
            //{
            //        rnd.NextBytes(datafragment);
            //    for (int i = 0; i < 1024; i++)
            //    {
            //        sw.Write(datafragment, 0, datafragment.Length);
            //        Console.WriteLine(i);
            //    }
            //}
            //return 0;
        }
    }
}

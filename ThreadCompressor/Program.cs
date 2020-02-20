using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;

namespace ThreadCompressor
{
    class Program
    {
        static int Main(string[] args)
        {
            string res = "";
            if (args.Length == 3)
            {
                if(args[0].ToLower() == "compress"|| args[0].ToLower() == "decompress")
                {
                    res = new GzWorker(args[1], args[2]).Start(args[0].ToLower() == "compress" ? CompressionMode.Compress : CompressionMode.Decompress);
                }
                else
                {
                    res = "Неверно задан аргумент определяющий метод обработки файла, укажите compress или decompress.";
                }
            }
            else res = "Неверно заданы аргументы.";
            
            //Thread.Sleep(2000);
            //res = new GzWorker("1.resS.cgz", "1.resS").Start(CompressionMode.Decompress);
            Console.WriteLine(string.IsNullOrEmpty(res) ? "OK" : res);
            Console.ReadKey();
            return string.IsNullOrEmpty(res) ? 0 : 1;
        }
    }
}

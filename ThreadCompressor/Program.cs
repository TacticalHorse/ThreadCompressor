using System;
using System.IO.Compression;

namespace ThreadCompressor
{
    class Program
    {
        static int Main(string[] args)
        {
            Console.Clear();
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
            
            Console.WriteLine(string.IsNullOrEmpty(res) ? "OK" : res);
            return string.IsNullOrEmpty(res) ? 0 : 1;
        }
    }
}

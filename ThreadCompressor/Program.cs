using System;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace ThreadCompressor
{
    class Program
    {
        static int Main(string[] args)
        {
            //GzWorker a = null;
            //Console.Clear();
            //string res = "";

            //if (Marshal.SizeOf(typeof(IntPtr)) != 8) { Console.WriteLine("Приложение должно быть собрано для систем x64"); return 1; }

            //if (args.Length == 3)
            //{
            //    if (args[0].ToLower() == "compress" || args[0].ToLower() == "decompress")
            //    {
            //        a = new GzWorker(args[1], args[2]);
            //        res = a.Start(args[0].ToLower() == "compress" ? CompressionMode.Compress : CompressionMode.Decompress);
            //    }
            //    else
            //    {
            //        res = "Неверно задан аргумент определяющий метод обработки файла, укажите compress или decompress.";
            //    }
            //}
            //else res = "Неверно заданы аргументы.";
            //Console.WriteLine(string.IsNullOrEmpty(res) ? "OK" : res);
            //return string.IsNullOrEmpty(res) ? 0 : 1;

            new GzWorker("In.f", "Com.f").Start(CompressionMode.Compress);
            new GzWorker("Com.f", "Out.f").Start(CompressionMode.Decompress);
            Tools.GetHash("In.f");
            Tools.GetHash("Out.f");
            return 0;
        }
    }
}

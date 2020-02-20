using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ThreadCompressor
{
    class Program
    {
        static int Main(string[] args)
        {
            //if (!File.Exists("datain.txt"))
            {
                //Random rnd = new Random();
                //using (var Stream = File.Create("datain.txt"))
                //{
                //    var data = new byte[100000];
                //    for (int j = 1; j < 134; j++)
                //    {
                //        rnd.NextBytes(data);
                //        Stream.Write(data, 0, 100000);
                //        Console.Clear();
                //        Console.WriteLine((double)j / 134d * 100 + "%");
                //    }
                //}
                //using (var sw = File.CreateText("datain.txt"))
                //{
                //    for (int i = 0; i < 1000000; i++)
                //    {
                //        sw.WriteLine(new StringBuilder("asdfghjkl"));
                //    }
                //}
            }
            Console.Clear();
            string res = "";
            Console.Read();
            res = GzWorker.Start(CompressionMode.Compress, "1.resS", "1.resS.cgz");
            Thread.Sleep(2000);
            res = GzWorker.Start(CompressionMode.Decompress, "1.resS.cgz", "1.resS");
            Console.WriteLine(res);
            Console.WriteLine(CalculateMD5("1.resS"));
            Console.WriteLine(CalculateMD5("11.resS"));
            Console.ReadKey();

            return string.IsNullOrEmpty(res) ? 0 : 1;
        }
        static string CalculateMD5(string filename)
        {
            using (var md5 = MD5.Create())
            {
                using (var stream = File.OpenRead(filename))
                {
                    var hash = md5.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        class GzWorker
        {
            static FileStream SR;
            static FileStream SW;

            static int blockcount;
            static int readblock, writeblock, currentblock;
            static int blocksize;

            static ThreadHandler[] ThreadPool;

            static volatile byte[][] inputdata;
            static volatile byte[][] outputdata;

            public static CompressionMode CompressionMode { get; set; }
            public static string InputFileName { get; set; }
            public static string OutputFileName { get; set; }

            public static string Start(CompressionMode CompressionMode, string InputFileName, string OutputFileName)
            {
                Stopwatch watch = Stopwatch.StartNew();
                readblock = writeblock = currentblock = 0;
                //if (!File.Exists(InputFileName)) return "Исходный файл не найден";
                using (FileStream SR = File.OpenRead(InputFileName))
                {
                    //if (File.Exists(OutputFileName)) return "Уже существует";
                    using (FileStream SW = File.Create(OutputFileName))
                    {
                        if (Environment.ProcessorCount > 4)
                            ThreadPool = new ThreadHandler[Environment.ProcessorCount - 1];
                        else
                            ThreadPool = new ThreadHandler[4];
                        for (int i = 0; i < ThreadPool.Length; i++)
                        {
                            ThreadPool[i] = new ThreadHandler(CompressionMode);
                        }

                        if (CompressionMode == CompressionMode.Decompress)
                        {
                            byte[] data = new byte[4];
                            SR.Read(data, 0, 4);
                            blocksize = BitConverter.ToInt32(data, 0);
                            SR.Read(data, 0, 4);
                            blockcount = BitConverter.ToInt32(data, 0);
                        }
                        else
                        {
                            blocksize = 1048576; //мб
                            blockcount = (int)Math.Ceiling(((double)new FileInfo(InputFileName).Length) / blocksize);
                            SW.Write(BitConverter.GetBytes(blocksize), 0, 4);
                            SW.Write(BitConverter.GetBytes(blockcount), 0, 4);
                        }

                        inputdata = new byte[blockcount][];
                        outputdata = new byte[blockcount][];
                        ThreadHandler.BlockSize = blocksize;
                        while (true)
                        {
                            if (Process.GetCurrentProcess().WorkingSet64 < 1024 * 1024 * 100)//ограничение по оперативке, 50 мало - виснет, больше 100 смысла нет, прироста не дает
                            {
                                for (int i = 0; i < ThreadPool.Length; i++)
                                {
                                    if (readblock < blockcount)
                                    {
                                        if (CompressionMode == CompressionMode.Decompress)
                                        {
                                            var sizedata = new byte[4];
                                            SR.Read(sizedata, 0, 4);
                                            int size = BitConverter.ToInt32(sizedata, 0);
                                            var arr = new byte[size];
                                            SR.Read(arr, 0, size);
                                            inputdata[readblock] = arr;
                                            readblock++;
                                        }
                                        else
                                        {
                                            if (readblock + 1 == blockcount)
                                            {
                                                int lastblocksize = (int)(new FileInfo(InputFileName).Length - readblock * blocksize);
                                                var arr = new byte[lastblocksize];
                                                SR.Read(arr, 0, lastblocksize);
                                                inputdata[readblock] = arr;
                                                readblock++;
                                            }
                                            else
                                            {
                                                var arr = new byte[blocksize];
                                                SR.Read(arr, 0, blocksize);
                                                inputdata[readblock] = arr;
                                                readblock++;
                                            }
                                        }
                                    }
                                }
                                SR.Flush();
                            }


                            for (int i = 0; i < ThreadPool.Length; i++)
                            {
                                if (writeblock < readblock)
                                {
                                    if (ThreadPool[i].Index > -1 && ThreadPool[i].InputData == null)
                                    {
                                        outputdata[ThreadPool[i].Index] = ThreadPool[i].OutputData;
                                        ThreadPool[i].OutputData = null;
                                        ThreadPool[i].Index = -1;
                                    }
                                }
                                if (currentblock < readblock)
                                {
                                    if (ThreadPool[i].Index == -1)
                                    {
                                        ThreadPool[i].Index = currentblock;
                                        ThreadPool[i].InputData = inputdata[currentblock];
                                        inputdata[currentblock] = null;
                                        currentblock++;
                                    }
                                }
                            }

                            for (int i = 0; i < ThreadPool.Length / 2; i++)
                            {
                                if (writeblock < currentblock && outputdata[writeblock] != null)
                                {
                                    SW.Write(outputdata[writeblock], 0, outputdata[writeblock].Length);
                                    outputdata[writeblock] = null;
                                    writeblock++;
                                }
                                SW.Flush();
                            }

                            Console.Clear();
                            Console.WriteLine(string.Format("{0:N2}",((double)writeblock / (double)blockcount) * 100) + "%");
                            Thread.Sleep(1);
                            GC.Collect();
                            if (writeblock == blockcount) break;

                        }
                        for (int i = 0; i < ThreadPool.Length; i++) 
                        {
                            ThreadPool[i].Stop();
                            ThreadPool[i] = null;
                        }

                        GC.Collect();
                        watch.Stop();
                        Console.WriteLine(watch.ElapsedMilliseconds);
                        return "0";
                    }
                }
            }
        }

        class ThreadHandler
        {
            private bool iswork;
            private bool isstoped;
            private CompressionMode Mode;
            private Thread thread;

            public long Index = -1; //Индекс блока
            public static int BlockSize = -1; //Размер блока
            public byte[] InputData;
            public byte[] OutputData;

            public ThreadHandler(CompressionMode Mode)
            {
                iswork = true;
                this.Mode = Mode;
                thread = new Thread(Work);
                thread.Start();
            }

            private void Work()
            {
                while (iswork)
                {
                    if (InputData != null)
                    {
                        if (Mode == CompressionMode.Compress)
                        {
                            using (MemoryStream Output = new MemoryStream())
                            {
                                using (GZipStream gzstream = new GZipStream(Output, CompressionMode.Compress, true))
                                {
                                    gzstream.Write(InputData, 0, InputData.Length);
                                }
                                OutputData = Output.GetBuffer();
                            }
                            CutEmptyPart(ref OutputData);
                            AddCustomHead(ref OutputData);
                        }
                        else
                        {
                            var buffer = new byte[BlockSize];
                            int count = 0;
                            using (GZipStream gzstream = new GZipStream(new MemoryStream(InputData), CompressionMode.Decompress))
                            {
                                count = gzstream.Read(buffer, 0, buffer.Length);
                            }
                            using (MemoryStream memory = new MemoryStream())
                            {
                                if (count > 0) memory.Write(buffer, 0, count);
                                OutputData = memory.GetBuffer();
                            }
                        }

                        InputData = null;
                        GC.Collect();
                    }
                    else Thread.Sleep(1);
                }
                isstoped = true;
            }
            public void Stop()
            {
                iswork = false;
                while (!isstoped) Thread.Sleep(1);
            }

            private void CutEmptyPart(ref byte[] input)
            {
                int index = IndexOfFileTale(input);
                if (index > -1) Array.Resize(ref input, index);
            }
            private void AddCustomHead(ref byte[] input)
            {
                byte[] intBytes = BitConverter.GetBytes(input.Length);
                Array.Resize(ref intBytes, input.Length + 4);
                Array.Copy(input, 0, intBytes, 4, input.Length);
                input = intBytes;
            }
            private int IndexOfFileTale(byte[] inputArray)
            {
                if (inputArray == null || inputArray.Length < 4) return -1;
                int index = inputArray.Length - 1;
                while (inputArray[index] == 0x00 && inputArray[index - 1] == 0x00)
                {
                    index = index - 2;
                }
                return index;
            }
        }
    }
}

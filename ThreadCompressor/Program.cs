using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace ThreadCompressor
{
    class Program
    {
        static ThreadHandler[] ThreadPool;
        static FileStream SR;
        static FileStream SW;

        static volatile byte[][] inputData;
        static volatile byte[][] outputData;

        static int blocks;
        static int readblock = 0, writeblock = 0, currentblock = 0;

        const int mb = 1048576;

        static void Main(string[] args)
        {
            if (!File.Exists("datain.txt"))
            {
                Random rnd = new Random();
                using (var Stream = File.Create("datain.txt"))
                {
                    var data = new byte[10000000];
                    for (int j = 1; j < 101; j++)
                    {
                        rnd.NextBytes(data);
                        Stream.Write(data, 0, 10000000);
                        Console.Clear();
                        Console.WriteLine((double)j / 100d * 100 + "%");
                    }
                }
            }
            Console.WriteLine("gen");
            Console.ReadKey();

            if (Environment.ProcessorCount > 4)
            {
                ThreadPool = new ThreadHandler[Environment.ProcessorCount - 1];
                for (int i = 0; i < ThreadPool.Length; i++)
                {
                    ThreadPool[i] = new ThreadHandler(CompressionMode.Compress);
                }
            }
            blocks = (int)Math.Ceiling(((double)new FileInfo("datain.txt").Length) / 1024 / 1024);

            inputData = new byte[blocks][];
            outputData = new byte[blocks][];

            SR = File.OpenRead("datain.txt");
            SW = File.Create("dataout.gz");
            while (true)
            {
                if (Process.GetCurrentProcess().WorkingSet64 < 1024 * 1024 * 200)
                {
                    for (int i = 0; i < ThreadPool.Length; i++)
                    {
                        if (readblock < blocks)
                        {
                            var arr = new byte[1024 * 1024];
                            SR.Read(arr, 0, mb);
                            inputData[readblock] = arr;
                            readblock++;
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
                            outputData[ThreadPool[i].Index] = ThreadPool[i].OutputData;
                            ThreadPool[i].OutputData = null;
                            ThreadPool[i].Index = -1;
                        }
                    }
                    if (currentblock < readblock)
                    {
                        if (ThreadPool[i].Index == -1)
                        {
                            ThreadPool[i].Index = currentblock;
                            ThreadPool[i].InputData = inputData[currentblock];
                            inputData[currentblock] = null;
                            currentblock++;
                        }
                    }
                }

                for (int i = 0; i < ThreadPool.Length / 2; i++)
                {
                    if (writeblock < currentblock && outputData[writeblock] != null)
                    {
                        SW.Write(outputData[writeblock], 0, outputData[writeblock].Length);
                        outputData[writeblock] = null;
                        writeblock++;
                    }
                    SW.Flush();
                }
                if (writeblock == blocks) return;

                Console.Clear();
                Console.WriteLine(((double)currentblock / (double)blocks) * 100 + "%");
                Thread.Sleep(1);
                GC.Collect();

            }
            for (int i = 0; i < ThreadPool.Length; i++) ThreadPool[i].Stop();
            SR.Close();
            SW.Close();
        }

        class ThreadHandler
        {
            private bool iswork;
            private bool isstoped;
            private CompressionMode Mode;
            private Thread thread;
            private GZipStream gzstream;
            private MemoryStream instream;
            private MemoryStream outstream;
            public long Index = -1;
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
                        OutputData = new byte[1024 * 1024];
                        outstream = new MemoryStream(OutputData);
                        instream = new MemoryStream(InputData);
                        gzstream = new GZipStream(outstream, Mode);
                        gzstream.Write(OutputData, 0, 1024 * 1024);
                        InputData = null;
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
        }
    }
}

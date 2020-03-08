﻿using System;
using System.IO;
using System.Security.Cryptography;

namespace ThreadCompressor
{
    public static class Tools
    {
        public static void GetHash(string FileName)
        {
            using (var md5 = MD5.Create())
            {
                using (var stream = File.OpenRead(FileName))
                {
                    var hash = md5.ComputeHash(stream);
                    Console.WriteLine(BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant());
                }
            }
        }


        public static void CreateFile(long Size, string FileName)
        {
            byte[] basePart = new byte[32*1024*1024];
            new Random().NextBytes(basePart);

            long count = 0;
            using (var writer = File.Create(FileName))
            {
                while (count + basePart.Length < Size)
                {
                    writer.Write(basePart, 0, basePart.Length);
                    count += basePart.Length;
                }
                writer.Write(basePart, 0, (int)(Size - count));
            }
        }
    }
}

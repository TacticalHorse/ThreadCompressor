using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ThreadCompressor
{
    public static class Tools
    {
        public static string GetHash(string FileName)
        {
            using (var md5 = MD5.Create())
            {
                using (var stream = File.OpenRead(FileName))
                {
                    var hash = md5.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
        }
    }
}

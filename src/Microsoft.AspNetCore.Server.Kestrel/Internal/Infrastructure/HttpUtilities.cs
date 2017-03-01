// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Http;

namespace Microsoft.AspNetCore.Server.Kestrel.Internal.Infrastructure
{
    public static class HttpUtilities
    {
        public const string Http10Version = "HTTP/1.0";
        public const string Http11Version = "HTTP/1.1";

        // readonly primitive statics can be Jit'd to consts https://github.com/dotnet/coreclr/issues/1079
        private readonly static ulong _httpConnectMethodLong = GetAsciiStringAsLong("CONNECT ");
        private readonly static ulong _httpDeleteMethodLong = GetAsciiStringAsLong("DELETE \0");
        private const uint _httpGetMethodInt = 542393671; // retun of GetAsciiStringAsInt("GET "); const results in better codegen
        private readonly static ulong _httpHeadMethodLong = GetAsciiStringAsLong("HEAD \0\0\0");
        private readonly static ulong _httpPatchMethodLong = GetAsciiStringAsLong("PATCH \0\0");
        private readonly static ulong _httpPostMethodLong = GetAsciiStringAsLong("POST \0\0\0");
        private readonly static ulong _httpPutMethodLong = GetAsciiStringAsLong("PUT \0\0\0\0");
        private readonly static ulong _httpOptionsMethodLong = GetAsciiStringAsLong("OPTIONS ");
        private readonly static ulong _httpTraceMethodLong = GetAsciiStringAsLong("TRACE \0\0");

        private const ulong _http10VersionLong = 3471766442030158920; // GetAsciiStringAsLong("HTTP/1.0"); const results in better codegen
        private const ulong _http11VersionLong = 3543824036068086856; // GetAsciiStringAsLong("HTTP/1.1"); const results in better codegen

        private readonly static ulong _mask8Chars = GetMaskAsLong(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff });
        private readonly static ulong _mask7Chars = GetMaskAsLong(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00 });
        private readonly static ulong _mask6Chars = GetMaskAsLong(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00, 0x00 });
        private readonly static ulong _mask5Chars = GetMaskAsLong(new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00 });
        private readonly static ulong _mask4Chars = GetMaskAsLong(new byte[] { 0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00 });

        private readonly static Tuple<ulong, ulong, HttpMethod, int>[] _knownMethods = new Tuple<ulong, ulong, HttpMethod, int>[8];

        static HttpUtilities()
        {
            _knownMethods[0] = Tuple.Create(_mask4Chars, _httpPutMethodLong, HttpMethod.Put, 3);
            _knownMethods[1] = Tuple.Create(_mask5Chars, _httpPostMethodLong, HttpMethod.Post, 4);
            _knownMethods[2] = Tuple.Create(_mask5Chars, _httpHeadMethodLong, HttpMethod.Head, 4);
            _knownMethods[3] = Tuple.Create(_mask6Chars, _httpTraceMethodLong, HttpMethod.Trace, 5);
            _knownMethods[4] = Tuple.Create(_mask6Chars, _httpPatchMethodLong, HttpMethod.Patch, 5);
            _knownMethods[5] = Tuple.Create(_mask7Chars, _httpDeleteMethodLong, HttpMethod.Delete, 6);
            _knownMethods[6] = Tuple.Create(_mask8Chars, _httpConnectMethodLong, HttpMethod.Connect, 7);
            _knownMethods[7] = Tuple.Create(_mask8Chars, _httpOptionsMethodLong, HttpMethod.Options, 7);
        }

        private unsafe static ulong GetAsciiStringAsLong(string str)
        {
            Debug.Assert(str.Length == 8, "String must be exactly 8 (ASCII) characters long.");

            var bytes = Encoding.ASCII.GetBytes(str);

            fixed (byte* ptr = &bytes[0])
            {
                return *(ulong*)ptr;
            }
        }

        private unsafe static uint GetAsciiStringAsInt(string str)
        {
            Debug.Assert(str.Length == 4, "String must be exactly 4 (ASCII) characters long.");

            var bytes = Encoding.ASCII.GetBytes(str);

            fixed (byte* ptr = &bytes[0])
            {
                return *(uint*)ptr;
            }
        }

        private unsafe static ulong GetMaskAsLong(byte[] bytes)
        {
            Debug.Assert(bytes.Length == 8, "Mask must be exactly 8 bytes long.");

            fixed (byte* ptr = bytes)
            {
                return *(ulong*)ptr;
            }
        }

        public static string GetAsciiStringEscaped(this Span<byte> span, int maxChars)
        {
            var sb = new StringBuilder();

            int i;
            for (i = 0; i < Math.Min(span.Length, maxChars); ++i)
            {
                var ch = span[i];
                sb.Append(ch < 0x20 || ch >= 0x7F ? $"<0x{ch:X2}>" : ((char)ch).ToString());
            }

            if (span.Length > maxChars)
            {
                sb.Append("...");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Checks that up to 8 bytes from <paramref name="span"/> correspond to a known HTTP method.
        /// </summary>
        /// <remarks>
        /// A "known HTTP method" can be an HTTP method name defined in the HTTP/1.1 RFC.
        /// Since all of those fit in at most 8 bytes, they can be optimally looked up by reading those bytes as a long. Once
        /// in that format, it can be checked against the known method.
        /// The Known Methods (CONNECT, DELETE, GET, HEAD, PATCH, POST, PUT, OPTIONS, TRACE) are all less than 8 bytes
        /// and will be compared with the required space. A mask is used if the Known method is less than 8 bytes.
        /// To optimize performance the GET method will be checked first.
        /// </remarks>
        /// <returns><c>true</c> if the input matches a known string, <c>false</c> otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool GetKnownMethod(this Span<byte> span, out HttpMethod method, out int length)
        {
            if (span.TryRead<uint>(out var possiblyGet))
            {
                if (possiblyGet == _httpGetMethodInt)
                {
                    length = 3;
                    method = HttpMethod.Get;
                    return true;
                }
            }

            if (span.TryRead<ulong>(out var value))
            {
                foreach (var x in _knownMethods)
                {
                    if ((value & x.Item1) == x.Item2)
                    {
                        method = x.Item3;
                        length = x.Item4;
                        return true;
                    }
                }
            }

            method = HttpMethod.Custom;
            length = 0;
            return false;
        }

        /// <summary>
        /// Checks 9 bytes from <paramref name="span"/>  correspond to a known HTTP version.
        /// </summary>
        /// <remarks>
        /// A "known HTTP version" Is is either HTTP/1.0 or HTTP/1.1.
        /// Since those fit in 8 bytes, they can be optimally looked up by reading those bytes as a long. Once
        /// in that format, it can be checked against the known versions.
        /// The Known versions will be checked with the required '\r'.
        /// To optimize performance the HTTP/1.1 will be checked first.
        /// </remarks>
        /// <returns><c>true</c> if the input matches a known string, <c>false</c> otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool GetKnownVersion(this Span<byte> span, out HttpVersion knownVersion, out byte length)
        {
            if (span.TryRead<ulong>(out var version))
            {
                if (version == _http11VersionLong)
                {
                    length = sizeof(ulong);
                    knownVersion = HttpVersion.Http11;
                }
                else if (version == _http10VersionLong)
                {
                    length = sizeof(ulong);
                    knownVersion = HttpVersion.Http10;
                }
                else
                {
                    length = 0;
                    knownVersion = HttpVersion.Unknown;
                    return false;
                }

                if (span[sizeof(ulong)] == (byte)'\r')
                {
                    return true;
                }
            }

            knownVersion = HttpVersion.Unknown;
            length = 0;
            return false;
        }

        public static string VersionToString(HttpVersion httpVersion)
        {
            switch (httpVersion)
            {
                case HttpVersion.Http10:
                    return Http10Version;
                case HttpVersion.Http11:
                    return Http11Version;
                default:
                    throw new ArgumentOutOfRangeException(nameof(httpVersion), httpVersion, null);
            }
        }
        public static string MethodToString(HttpMethod method)
        {
            switch (method)
            {
                case HttpMethod.Get:
                    return HttpMethods.Get;
                case HttpMethod.Put:
                    return HttpMethods.Put;
                case HttpMethod.Delete:
                    return HttpMethods.Delete;
                case HttpMethod.Post:
                    return HttpMethods.Post;
                case HttpMethod.Head:
                    return HttpMethods.Head;
                case HttpMethod.Trace:
                    return HttpMethods.Trace;
                case HttpMethod.Patch:
                    return HttpMethods.Patch;
                case HttpMethod.Connect:
                    return HttpMethods.Connect;
                case HttpMethod.Options:
                    return HttpMethods.Options;
                default:
                    throw new ArgumentOutOfRangeException(nameof(method), method, null);
            }
        }
    }
}

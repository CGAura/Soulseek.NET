﻿// <copyright file="NetInfo.cs" company="JP Dillingham">
//     Copyright (c) JP Dillingham. All rights reserved.
//
//     This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as
//     published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty
//     of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the GNU General Public License for more details.
//
//     You should have received a copy of the GNU General Public License along with this program. If not, see https://www.gnu.org/licenses/.
// </copyright>

namespace Soulseek.Messaging.Messages
{
    using Soulseek.Exceptions;

    internal sealed class DistributedSearchRequest
    {
        public DistributedSearchRequest(string username, int token, string query)
        {
            Username = username;
            Token = token;
            Query = query;
        }

        public string Username { get; }
        public int Token { get; }
        public string Query { get; }

        /// <summary>
        ///     Creates a new instance of <see cref="DistributedSearchRequest"/> from the specified <paramref name="bytes"/>.
        /// </summary>
        /// <param name="bytes">The byte array from which to parse.</param>
        /// <returns>The created instance.</returns>
        public static DistributedSearchRequest FromByteArray(byte[] bytes)
        {
            var reader = new MessageReader<MessageCode.Distributed>(bytes);
            var code = reader.ReadCode();

            if (code != MessageCode.Distributed.SearchRequest)
            {
                throw new MessageException($"Message Code mismatch creating Distributed Search Request (expected: {(int)MessageCode.Distributed.SearchRequest}, received: {(int)code}.");
            }

            // nobody knows what this is.
            reader.ReadInteger();

            var username = reader.ReadString();
            var token = reader.ReadInteger();
            var query = reader.ReadString();

            return new DistributedSearchRequest(username, token, query);
        }
    }
}
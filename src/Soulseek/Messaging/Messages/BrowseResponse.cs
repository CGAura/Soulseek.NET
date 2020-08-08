﻿// <copyright file="BrowseResponse.cs" company="JP Dillingham">
//     Copyright (c) JP Dillingham. All rights reserved.
//
//     This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License
//     as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty
//     of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the GNU General Public License for more details.
//
//     You should have received a copy of the GNU General Public License along with this program. If not, see https://www.gnu.org/licenses/.
// </copyright>

namespace Soulseek.Messaging.Messages
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Soulseek.Exceptions;

    /// <summary>
    ///     The response to a peer browse request.
    /// </summary>
    internal sealed class BrowseResponse
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="BrowseResponse"/> class.
        /// </summary>
        /// <param name="directoryCount">The optional directory count.</param>
        /// <param name="directoryList">The optional directory list.</param>
        public BrowseResponse(int directoryCount, IEnumerable<Directory> directoryList = null)
        {
            DirectoryCount = directoryCount;
            DirectoryList = directoryList ?? Enumerable.Empty<Directory>();
        }

        /// <summary>
        ///     Gets the list of directories.
        /// </summary>
        public IReadOnlyCollection<Directory> Directories => DirectoryList.ToList().AsReadOnly();

        /// <summary>
        ///     Gets the number of directories.
        /// </summary>
        public int DirectoryCount { get; }

        private IEnumerable<Directory> DirectoryList { get; }

        /// <summary>
        ///     Creates a new instance of <see cref="BrowseResponse"/> from the specified <paramref name="bytes"/>.
        /// </summary>
        /// <param name="bytes">The byte array from which to parse.</param>
        /// <returns>The parsed instance.</returns>
        public static BrowseResponse FromByteArray(byte[] bytes)
        {
            var reader = new MessageReader<MessageCode.Peer>(bytes);
            var code = reader.ReadCode();

            if (code != MessageCode.Peer.BrowseResponse)
            {
                throw new MessageException($"Message Code mismatch creating Peer Browse Response (expected: {(int)MessageCode.Peer.BrowseResponse}, received: {(int)code}");
            }

            reader.Decompress();

            var directoryCount = reader.ReadInteger();
            var directoryList = new List<Directory>();

            for (int i = 0; i < directoryCount; i++)
            {
                directoryList.Add(reader.ReadDirectory());
            }

            if (reader.HasMoreData)
            {
                _ = reader.ReadInteger();

                if (reader.HasMoreData)
                {
                    directoryCount = reader.ReadInteger();

                    for (int i = 0; i < directoryCount; i++)
                    {
                        directoryList.Add(reader.ReadDirectory());
                    }
                }
            }

            return new BrowseResponse(directoryCount, directoryList);
        }

        /// <summary>
        ///     Constructs a <see cref="byte"/> array from this message.
        /// </summary>
        /// <returns>The constructed byte array.</returns>
        public byte[] ToByteArray()
        {
            var builder = new MessageBuilder()
                .WriteCode(MessageCode.Peer.BrowseResponse)
                .WriteInteger(DirectoryCount);

            foreach (var directory in Directories)
            {
                builder.WriteDirectory(directory);
            }

            builder.Compress();
            return builder.Build();
        }
    }
}
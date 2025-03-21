﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.IO;
using Nethermind.Db.Config;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Db
{
    public class RocksDbProvider : IDbProvider
    {
        public RocksDbProvider(string basePath, IDbConfig dbConfig, ILogManager logManager, bool useTraceDb, bool useReceiptsDb)
        {
            BlocksDb = new DbOnTheRocks(
                Path.Combine(basePath, DbOnTheRocks.BlocksDbPath),
                dbConfig, logManager);
            
            HeadersDb = new DbOnTheRocks(
                            Path.Combine(basePath, DbOnTheRocks.HeadersPath),
                            dbConfig, logManager);
            
            BlockInfosDb = new DbOnTheRocks(
                Path.Combine(basePath, DbOnTheRocks.BlockInfosDbPath),
                dbConfig, logManager);

            if (useReceiptsDb)
            {
                ReceiptsDb = new DbOnTheRocks(
                    Path.Combine(basePath, DbOnTheRocks.ReceiptsDbPath),
                    dbConfig, logManager);
            }
            else
            {
                ReceiptsDb = new ReadOnlyDb(new MemDb(), false);
            }

            StateDb = new StateDb(
                new DbOnTheRocks(Path.Combine(basePath, DbOnTheRocks.StateDbPath), dbConfig, logManager));
            
            CodeDb = new StateDb(
                new DbOnTheRocks(Path.Combine(basePath, DbOnTheRocks.CodeDbPath), dbConfig, logManager));
            
            PendingTxsDb = new DbOnTheRocks(
                Path.Combine(basePath, DbOnTheRocks.PendingTxsDbPath),
                dbConfig, logManager);

            if (useTraceDb)
            {
                TraceDb = new DbOnTheRocks(
                    Path.Combine(basePath, DbOnTheRocks.TraceDbPath),
                    dbConfig, logManager);
            }
            else
            {
                TraceDb = new ReadOnlyDb(new MemDb(), false);
            }
        }
        
        public ISnapshotableDb StateDb { get; }
        public ISnapshotableDb CodeDb { get; }
        public IDb TraceDb { get; }
        public IDb ReceiptsDb { get; }
        public IDb BlocksDb { get; }
        public IDb HeadersDb { get; }
        public IDb BlockInfosDb { get; }
        public IDb PendingTxsDb { get; }

        public void Dispose()
        {
            StateDb?.Dispose();
            CodeDb?.Dispose();
            ReceiptsDb?.Dispose();
            BlocksDb?.Dispose();
            HeadersDb?.Dispose();
            BlockInfosDb?.Dispose();
            PendingTxsDb?.Dispose();
            TraceDb?.Dispose();
        }
    }
}
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

namespace Nethermind.Store
{
    public class MemDbProvider: IDbProvider
    {
        public ISnapshotableDb StateDb { get; } = new StateDb();
        public ISnapshotableDb CodeDb { get; } = new StateDb();
        public IDb ReceiptsDb { get; } = new MemDb();
        public IDb BlocksDb { get; } = new MemDb();
        public IDb HeadersDb { get; } = new MemDb();
        public IDb BlockInfosDb { get; } = new MemDb();
        public IDb PendingTxsDb { get; } = new MemDb();
        public IDb TraceDb { get; } = new MemDb();

        public void Dispose()
        {
            StateDb?.Dispose();
            CodeDb?.Dispose();
            ReceiptsDb?.Dispose();
            BlocksDb?.Dispose();
            BlockInfosDb?.Dispose();
            PendingTxsDb?.Dispose();
        }
    }
}
//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Linq;
using Nethermind.Config;
using Nethermind.Core;

namespace Nethermind.KeyStore.Config
{
    /// <summary>
    /// https://medium.com/@julien.maffre/what-is-an-ethereum-keystore-file-86c8c5917b97
    /// https://github.com/ethereum/wiki/wiki/Web3-Secret-Storage-Definition
    /// </summary>
    public interface IKeyStoreConfig : IConfig
    {
        [ConfigItem(Description = "Directory to store keys in.", DefaultValue = "keystore")]
        string KeyStoreDirectory { get; set; }

        [ConfigItem(Description = "See https://github.com/ethereum/wiki/wiki/Web3-Secret-Storage-Definition", DefaultValue = "UTF-8")]
        string KeyStoreEncoding { get; set; }

        [ConfigItem(Description = "See https://github.com/ethereum/wiki/wiki/Web3-Secret-Storage-Definition", DefaultValue = "scrypt")]
        string Kdf { get; set; }

        [ConfigItem(Description = "See https://github.com/ethereum/wiki/wiki/Web3-Secret-Storage-Definition", DefaultValue = "aes-128-ctr")]
        string Cipher { get; set; }

        [ConfigItem(Description = "See https://github.com/ethereum/wiki/wiki/Web3-Secret-Storage-Definition", DefaultValue = "32")]
        int KdfparamsDklen { get; set; }

        [ConfigItem(Description = "See https://github.com/ethereum/wiki/wiki/Web3-Secret-Storage-Definition", DefaultValue = "262144")]
        int KdfparamsN { get; set; }

        [ConfigItem(Description = "See https://github.com/ethereum/wiki/wiki/Web3-Secret-Storage-Definition", DefaultValue = "1")]
        int KdfparamsP { get; set; }

        [ConfigItem(Description = "See https://github.com/ethereum/wiki/wiki/Web3-Secret-Storage-Definition", DefaultValue = "8")]
        int KdfparamsR { get; set; }

        [ConfigItem(Description = "See https://github.com/ethereum/wiki/wiki/Web3-Secret-Storage-Definition", DefaultValue = "32")]
        int KdfparamsSaltLen { get; set; }

        [ConfigItem(Description = "See https://github.com/ethereum/wiki/wiki/Web3-Secret-Storage-Definition", DefaultValue = "128")]
        int SymmetricEncrypterBlockSize { get; set; }

        [ConfigItem(Description = "See https://github.com/ethereum/wiki/wiki/Web3-Secret-Storage-Definition", DefaultValue = "128")]
        int SymmetricEncrypterKeySize { get; set; }

        [ConfigItem(Description = "See https://github.com/ethereum/wiki/wiki/Web3-Secret-Storage-Definition", DefaultValue = "16")]
        int IVSize { get; set; }

        [ConfigItem(Description = "Plain private key to be used in test scenarios")]
        string TestNodeKey { get; set; }

        [ConfigItem(Description = "Account to be used by the block author / coinbase")]
        string BlockAuthorAccount { get; set; }

        [ConfigItem(Description = "Passwords to use to unlock accounts from the UnlockAccounts configuration item. Only used when no PasswordFiles provided.")]
        string[] Passwords { get; set; }

        [ConfigItem(Description = "Password files storing passwords to unlock the accounts from the UnlockAccounts configuration item")]
        string[] PasswordFiles { get; set; }

        [ConfigItem(Description = "Accounts to unlock on startup using provided PasswordFiles and Passwords")]
        string[] UnlockAccounts { get; set; }
    }

    public static class KeyStoreConfigExtensions
    {
        public static int FindUnlockAccountIndex(this IKeyStoreConfig keyStoreConfig, Address address)
        {
            return Array.IndexOf(
                (keyStoreConfig.UnlockAccounts ?? Array.Empty<string>())
                .Select(a => a.ToUpperInvariant())
                .ToArray(),
                address.ToString().ToUpperInvariant());
        }
    }
}
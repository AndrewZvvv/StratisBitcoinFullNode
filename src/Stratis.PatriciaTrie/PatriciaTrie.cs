﻿using System;

namespace Stratis.Patricia
{
    /// <summary>
    /// A merkle patricia trie implementation. Stores data in a trie and key/mapping structure
    /// in such a way that all of the data can be represented by a 32-bit hash.
    /// Full definition at: https://github.com/ethereum/wiki/wiki/Patricia-Tree
    /// </summary>
    public class PatriciaTrie : IPatriciaTrie
    {
        /// <summary>
        /// The key/value store used to store nodes and data by their hashes.
        /// </summary>
        private readonly ISource<byte[], byte[]> trieKvStore;

        /// <summary>
        /// The root of the trie.
        /// </summary>
        private Node root;

        public PatriciaTrie() : this((byte[])null) { }

        public PatriciaTrie(byte[] root) : this(new MemoryDictionarySource(), root) { }

        public PatriciaTrie(ISource<byte[], byte[]> trieKvStore) : this(trieKvStore, null) { }

        public PatriciaTrie(ISource<byte[],byte[]> trieKvStore, byte[] root)
        {
            this.trieKvStore = trieKvStore;
            this.SetRootHash(root);
        }

        /// <inheritdoc />
        public void SetRootHash(byte[] hash)
        {
            if (hash != null && !new ByteArrayComparer().Equals(hash, HashHelper.EmptyTrieHash))
            {
                this.root = new Node(hash, this.trieKvStore);
            }
            else
            {
                this.root = null;
            }
        }

        /// <inheritdoc />
        public byte[] GetRootHash()
        {
            this.Encode();
            return this.root != null ? this.root.Hash : HashHelper.EmptyTrieHash;
        }

        /// <inheritdoc />
        public byte[] Get(byte[] key)
        {
            if (!this.HasRoot())
                return null;
            Key k = Key.FromNormal(key);
            return this.Get(this.root, k);
        }

        /// <inheritdoc />
        public void Put(byte[] key, byte[] value)
        {
            Key k = Key.FromNormal(key);
            if (this.root == null)
            {
                if (value != null && value.Length > 0)
                {
                    this.root = new Node(k, value, this.trieKvStore);
                }
            }
            else
            {
                if (value == null || value.Length == 0)
                {
                    this.root = this.Delete(this.root, k);
                }
                else
                {
                    this.root = this.Insert(this.root, k, value);
                }
            }
        }

        /// <inheritdoc />
        public void Delete(byte[] key)
        {
            Key k = Key.FromNormal(key);
            if (this.root != null)
            {
                this.root = this.Delete(this.root, k);
            }
        }

        /// <inheritdoc />
        public bool Flush()
        {
            if (this.root != null && this.root.Dirty)
            {
                // persist all dirty Nodes to underlying Source
                this.Encode();
                // release all Trie Node instances for GC
                this.root = new Node(this.root.Hash, this.trieKvStore);
                return true;
            }
            else
            {
                return false;
            }
        }

        private bool HasRoot()
        {
            return this.root != null && this.root.ResolveCheck();
        }

        private byte[] Get(Node n, Key k)
        {
            if (n == null)
                return null;

            NodeType type = n.NodeType;

            if (type == NodeType.BranchNode)
            {
                if (k.IsEmpty)
                    return n.BranchNodeGetValue();

                Node childNode = n.BranchNodeGetChild(k.GetHex(0));
                return this.Get(childNode, k.Shift(1));
            }
            else
            {
                Key k1 = k.MatchAndShift(n.KvNodeGetKey());
                if (k1 == null) return null;
                if (type == NodeType.KeyValueNodeValue)
                {
                    return k1.IsEmpty ? n.KvNodeGetValue() : null;
                }
                else
                {
                    return this.Get(n.KvNodeGetChildNode(), k1);
                }
            }
        }

        private Node Insert(Node n, Key k, object NodeOrValue)
        {
            NodeType type = n.NodeType;
            if (type == NodeType.BranchNode)
            {
                if (k.IsEmpty) return n.BranchNodeSetValue((byte[])NodeOrValue);
                Node childNode = n.BranchNodeGetChild(k.GetHex(0));
                if (childNode != null)
                {
                    return n.BranchNodeSetChild(k.GetHex(0), this.Insert(childNode, k.Shift(1), NodeOrValue));
                }
                else
                {
                    Key childKey = k.Shift(1);
                    Node newChildNode;
                    if (!childKey.IsEmpty)
                    {
                        newChildNode = new Node(childKey, NodeOrValue, this.trieKvStore);
                    }
                    else
                    {
                        newChildNode = NodeOrValue is Node ?
                            (Node)NodeOrValue : new Node(childKey, NodeOrValue, this.trieKvStore);
                    }
                    return n.BranchNodeSetChild(k.GetHex(0), newChildNode);
                }
            }
            else
            {
                Key commonPrefix = k.GetCommonPrefix(n.KvNodeGetKey());
                if (commonPrefix.IsEmpty)
                {
                    Node newBranchNode = new Node(this.trieKvStore);
                    this.Insert(newBranchNode, n.KvNodeGetKey(), n.KvNodeGetValueOrNode());
                    this.Insert(newBranchNode, k, NodeOrValue);
                    n.Dispose();
                    return newBranchNode;
                }
                else if (commonPrefix.Equals(k))
                {
                    return n.KvNodeSetValueOrNode(NodeOrValue);
                }
                else if (commonPrefix.Equals(n.KvNodeGetKey()))
                {
                    this.Insert(n.KvNodeGetChildNode(), k.Shift(commonPrefix.Length), NodeOrValue);
                    return n.Invalidate();
                }
                else
                {
                    Node newBranchNode = new Node(this.trieKvStore);
                    Node newKvNode = new Node(commonPrefix, newBranchNode, this.trieKvStore);
                    // TODO can be optimized
                    this.Insert(newKvNode, n.KvNodeGetKey(), n.KvNodeGetValueOrNode());
                    this.Insert(newKvNode, k, NodeOrValue);
                    n.Dispose();
                    return newKvNode;
                }
            }
        }

        private Node Delete(Node n, Key k)
        {
            NodeType type = n.NodeType;
            Node newKvNode;
            if (type == NodeType.BranchNode)
            {
                if (k.IsEmpty)
                {
                    n.BranchNodeSetValue(null);
                }
                else
                {
                    int idx = k.GetHex(0);
                    Node child = n.BranchNodeGetChild(idx);
                    if (child == null) return n; // no key found

                    Node newNode = this.Delete(child, k.Shift(1));
                    n.BranchNodeSetChild(idx, newNode);
                    if (newNode != null) return n; // newNode != null thus number of children didn't decrease
                }

                // child Node or value was deleted and the branch Node may need to be compacted
                int compactIdx = n.BranchNodeCompactIdx();
                if (compactIdx < 0) return n; // no compaction is required

                // only value or a single child left - compact branch Node to kvNode
                n.Dispose();
                if (compactIdx == 16)
                { // only value left
                    return new Node(Key.Empty(true), n.BranchNodeGetValue(), this.trieKvStore);
                }
                else
                { // only single child left
                    newKvNode = new Node(Key.SingleHex(compactIdx), n.BranchNodeGetChild(compactIdx), this.trieKvStore);
                }
            }
            else
            { // n - kvNode
                Key k1 = k.MatchAndShift(n.KvNodeGetKey());
                if (k1 == null)
                {
                    // no key found
                    return n;
                }
                else if (type == NodeType.KeyValueNodeValue)
                {
                    if (k1.IsEmpty)
                    {
                        // delete this kvNode
                        n.Dispose();
                        return null;
                    }
                    else
                    {
                        // else no key found
                        return n;
                    }
                }
                else
                {
                    Node newChild = this.Delete(n.KvNodeGetChildNode(), k1);
                    if (newChild == null) throw new PatriciaTreeResolutionException("New node failed instantiation after deletion.");
                    newKvNode = n.KvNodeSetValueOrNode(newChild);
                }
            }

            // if we get here a new kvNode was created, now need to check
            // if it should be compacted with child kvNode
            Node nChild = newKvNode.KvNodeGetChildNode();
            if (nChild.NodeType != NodeType.BranchNode)
            {
                // two kvNodes should be compacted into a single one
                Key newKey = newKvNode.KvNodeGetKey().Concat(nChild.KvNodeGetKey());
                Node newNode = new Node(newKey, nChild.KvNodeGetValueOrNode(), this.trieKvStore);
                nChild.Dispose();
                return newNode;
            }
            else
            {
                // no compaction needed
                return newKvNode;
            }
        }

        private void Encode()
        {
            this.root?.Encode();
        }
    }
}

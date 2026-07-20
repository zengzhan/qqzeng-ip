using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace qqzengPgUI.ipdb8
{
    /// <summary>
    /// V6 Builder optimized for V12 comparison.
    /// Self-contained implementation including Trie and Helper.
    /// </summary>
    public class IPDBBuilderV6
    {
        public static async Task Build(string sourcePath, string targetDbPath)
        {
            try
            {
                var tree = new IpDbTree();
                var geoDict = new Dictionary<string, int>(StringComparer.Ordinal);
                var geoList = new List<string>();
                int firstGeoPartCount = -1;
                long lineNo = 0;

                using (var reader = new StreamReader(sourcePath, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lineNo++;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var parts = line.Split('\t');
                        if (parts.Length < 3) continue;

                        string startIpStr = parts[0];
                        string endIpStr = parts[1];
                        // If file uses Ints in col 2,3 (standard IPDB), parse them
                        if (long.TryParse(parts[2], out long s) && long.TryParse(parts[3], out long e))
                        {
                            // Convert to string for RangeToCidr (or we could optimize to use ints directly)
                            // To keep V6 logic 'compatible', we convert back to string for now.
                            // Or better: implement AddRange on Trie using Ints.
                            // Let's us AddRange(uint start, uint end, int geoId)
                            uint su = (uint)s;
                            uint eu = (uint)e;
                            
                            string geoInfo = string.Join("|", parts.Skip(4));
                            if (!geoDict.TryGetValue(geoInfo, out int geoId))
                            {
                                geoId = geoList.Count + 1; // 0 is empty
                                geoDict[geoInfo] = geoId;
                                geoList.Add(geoInfo);
                                if (firstGeoPartCount == -1) firstGeoPartCount = geoInfo.Split('|').Length;
                            }
                            
                            tree.InsertRange(su, eu, geoId);
                        }
                    }
                    if (lineNo % 100000 == 0) Console.WriteLine($"[V6] Read {lineNo} lines, Nodes: {tree.nodeCount}");
                }

                if (firstGeoPartCount == -1) firstGeoPartCount = 1;
                var newArray = new string[geoList.Count + 1];
                newArray[0] = string.Join("|", Enumerable.Repeat("", firstGeoPartCount));
                for (int i = 0; i < geoList.Count; i++) newArray[i + 1] = geoList[i];

                tree.AssignNodeNumbers(tree.root);

                int totalNodes = tree.nodeCount; // Rough estimate
                // Actual node count logic
                
                // Prefix Index
                var prefList = new List<int>(65536);
                for (int k = 0; k < 65536; k++)
                {
                    TrieNode node = tree.root;
                    // Move 16 levels
                    for (int i = 15; i >= 0; i--)
                    {
                        if (node == null) break;
                        int bit = (k >> i) & 1;
                        node = (bit == 0) ? node.Left : node.Right;
                    }
                    if (node == null) prefList.Add(0x800000); // Point to empty/default?
                    // Actually V6 uses 0x800000 as "End/Leaf" or "Geo"?
                    // V6 Searcher: (record & endMask) == endMask => Leaf.
                    // endMask = 0x800000.
                    // If leaf, record & complMask is GeoID.
                    // So if node is missing => Geo 0?
                    // If node is Leaf => (0x800000 | node.GeoIsp).
                    // If node is inner => node.NodeNum.
                    else if (node.IsLeaf) prefList.Add(0x800000 | node.GeoIsp);
                    else prefList.Add(node.NodeNum);
                }

                var listArr = tree.TraverseTreeToList(tree.root);

                using (FileStream fs = new FileStream(targetDbPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                using (BinaryWriter w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
                {
                    w.Write(BitConverter.GetBytes((uint)listArr.Count));
                    foreach (int index in prefList) w.Write(Int24(index));
                    foreach (var s in listArr) w.Write(Int48(s.Item1, s.Item2));
                    string str = string.Join("\t", newArray);
                    w.Write(Encoding.UTF8.GetBytes(str));
                }
                Console.WriteLine("[V6] Build Complete.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error V6: " + ex);
            }
        }
        
        static byte[] Int24(int num) {
            return new byte[] { (byte)(num >> 16), (byte)(num >> 8), (byte)num };
        }
        static byte[] Int48(int left, int right) {
            return new byte[] { 
                (byte)(left >> 16), (byte)(left >> 8), (byte)left,
                (byte)(right >> 16), (byte)(right >> 8), (byte)right
            };
        }

        class TrieNode
        {
            public TrieNode Left, Right;
            public int GeoIsp;
            public bool IsLeaf;
            public int NodeNum;
            // Depth not strictly needed for basic logic
        }

        class IpDbTree
        {
            public TrieNode root = new TrieNode();
            public int nodeCount = 0;

            public void InsertRange(uint start, uint end, int geoId)
            {
                // Standard range insertion into binary trie
                // We can use recursive approach covering the range
                Insert(root, 0, start, end, geoId, 0, 0xFFFFFFFF);
            }

            private void Insert(TrieNode node, int depth, uint start, uint end, int geoId, uint currentIp, uint mask)
            {
                if (start <= currentIp && end >= (currentIp | mask))
                {
                    // Covered
                    node.GeoIsp = geoId;
                    node.IsLeaf = true;
                    // Prune children? Yes, optimization.
                    node.Left = null;
                    node.Right = null; 
                    return;
                }
                if (depth >= 32) return; // Should be covered above
                
                // Need to split
                if (node.IsLeaf)
                {
                    // Existing leaf needs to be pushed down if it's different?
                    if (node.GeoIsp == geoId) return; // Same geo, don't overwrite if we don't handle priority? 
                    // Usually we overwrite with newer specific data?
                    // But here we are splitting an existing leaf. 
                    // If the existing leaf covers a range, and we insert a SUB-range with NEW geo.
                    // We need to push the old geo down.
                    // Allocate children
                    if (node.Left == null) { node.Left = new TrieNode { GeoIsp = node.GeoIsp, IsLeaf = true };  }
                    if (node.Right == null) { node.Right = new TrieNode { GeoIsp = node.GeoIsp, IsLeaf = true }; }
                    node.IsLeaf = false; // logic changes to inner
                }

                uint half = (mask >> 1);
                uint mid = currentIp | half; 
                // bit at (31 - depth)
                // Actually easier logic:
                // next bit is 0: range [currentIp, currentIp + half]
                // next bit is 1: range [currentIp + half + 1, currentIp + mask]
                
                // Check intersection with Left Child Range
                uint leftStart = currentIp;
                uint leftEnd = currentIp | half;
                if (Math.Max(start, leftStart) <= Math.Min(end, leftEnd))
                {
                     if(node.Left == null) { node.Left = new TrieNode(); nodeCount++; }
                     Insert(node.Left, depth + 1, start, end, geoId, leftStart, half);
                }

                // Check intersection with Right Child Range
                uint rightStart = (currentIp | half) + 1; // Actually leftEnd + 1?
                // Wait. 0xFFFFFFFF >> 1 = 0x7FFFFFFF.
                // currentIp | 0x7FFFFFFF is the end of left.
                // right start is currentIp + 0x80000000.
                // For depth 0: mask FFFFFFFF. half 7FFFFFFF.
                // left: 0..7FFFFFFF. right: 80000000..FFFFFFFF.
                uint bitVal = 1u << (31 - depth);
                uint rightBase = currentIp | bitVal;
                
                // My mask logic is relative.
                // Let's use simpler logic: 
                // bit at (31-depth).
                
                if (Math.Max(start, rightBase) <= Math.Min(end, rightBase | half))
                {
                     if(node.Right == null) { node.Right = new TrieNode(); nodeCount++; }
                     Insert(node.Right, depth + 1, start, end, geoId, rightBase, half);
                }
                
                // Merge optimization: if both children are leaves and same geo, merge back?
                if (node.Left != null && node.Right != null && 
                    node.Left.IsLeaf && node.Right.IsLeaf && 
                    node.Left.GeoIsp == node.Right.GeoIsp && node.Left.GeoIsp == geoId)
                {
                    node.IsLeaf = true;
                    node.GeoIsp = geoId;
                    node.Left = null;
                    node.Right = null;
                }
            }

           public void AssignNodeNumbers(TrieNode node)
            {
                // BFS or DFS? V6 uses BFS order likely for cache? 
                // V6 code: AssignNodeNumbers(tree.root).
                // Then TraverseTreeToList.
                // If checking searcher: 
                // record = ReadNode(record, bit);
                // offset = nodeNumber * 6.
                // ReadNode returns next node index.
                // So node index matters.
                // Usually BFS is used if we want children close? 
                // V6 Searcher logic: `ReadNode` reads from `data`.
                // If it constructs a flat array, we need to know the index.
                
                // Let's use a Queue for BFS assignment.
                int num = 0;
                var q = new Queue<TrieNode>();
                // Root is at local? V6 Searcher starts at `startIndex`.
                // Prefix table jumps to node index.
                // Root is virtual. Prefix table handles top 16 bits.
                // So we only assign numbers to nodes BELOW depth 16?
                // Or all nodes?
                // V6 code: `prefList` loops 16 bits.
                // `if (currentNode.CurrentDepth != 16 && !currentNode.IsLeaf) prefList.Add(0x800000)`
                // `else prefList.Add(currentNode.NodeNum)`
                // This implies Nodes at depth 16 (or higher if leaf reached earlier) have NodeNum.
                // AND nodes below depth 16 also have NodeNum.
                // But `ReadPref` returns an index into the node array.
                // So `NodeNum` is an index into the Node Data array.
                
                // We should assign NodeNums to all nodes reachable from the Prefix Table.
                // i.e., nodes at depth >= 16?
                // No, the prefix table *replaces* the top 16 levels of the tree.
                // So we only need to store nodes starting from depth 16?
                // Yes. The nodes passed as `root` to AssignNodeNumbers should likely be the ones at depth 16?
                // But V6 code `tree.AssignNodeNumbers(tree.root)` suggests it assigns all.
                // But then `prefList` uses `currentNode.NodeNum`.
                // If currentNode is at depth 16, its NodeNum is used.
                // If we traverse from Root, nodes at depth 0..15 have NodeNums too?
                // Typically "Prefix Optimization" means we skip top levels.
                // The searcher: `ReadPref(prefix)` gets `record`.
                // `while ... ReadNode(record ...)`.
                // So `record` IS a node index (or leaf).
                // So `prefList[prefix]` stores the Node Index of the node at 1.2.3.4 (depth 16).
                // So nodes at depth 16 MUST have a valid NodeNum.
                // Nodes ABOVE depth 16 (0..15) are NEVER accessed by `ReadNode` because we start from `ReadPref` (depth 16).
                // So we ONLY need to assign numbers and write nodes for depth >= 16.
                // EXCEPT if a Leaf is at depth < 16 (e.g. /8).
                // Then `prefList` entries for that /8 range will all point to... what?
                // `prefList.Add(0x800000 | node.GeoIsp)`. Leaf! 
                // So leaves at depth < 16 don't need NodeNum.
                // So only nodes that are NOT leaves at depth < 16 need to be traversed?
                // Actually, if node at depth 15 is not leaf, its children (depth 16) will be start of traversal.
                // So we basically collect all nodes at depth 16 (and their descendants) and serialize them.
                
                // My logic:
                // 1. Traverse top 16 bits. If leaf, `prefList` gets Leaf.
                // 2. If not leaf at depth 16, that node needs to be serialized.
                // 3. Serialize all descendants of depth 16 nodes.
                
                // Let's implement `AssignNodeNumbers` to do BFS starting from visible nodes.
                // But we need to assign numbers *globally* across all 65536 blocks for the single `listArr`.
                
                // Collect all "Roots of Subtrees" (nodes at depth 16).
                var roots = new List<TrieNode>();
                CollectSubtreeRoots(root, 0, roots);
                
                // Now BFS assign numbers to these roots and their children.
                var queue = new Queue<TrieNode>();
                foreach(var r in roots) queue.Enqueue(r);
                
                while(queue.Count > 0)
                {
                    var n = queue.Dequeue();
                    if (!n.IsLeaf)
                    {
                        n.NodeNum = num++; // Assign 0, 1, 2...
                        if(n.Left != null) queue.Enqueue(n.Left);
                        else { /* implicit empty? */ } // Handled in Traverse
                        
                        if(n.Right != null) queue.Enqueue(n.Right);
                    }
                }
            }
            
            private void CollectSubtreeRoots(TrieNode node, int depth, List<TrieNode> roots)
            {
                if (node == null) return;
                if (node.IsLeaf) return; // Leaf above or at 16 doesn't need numbering
                if (depth == 16)
                {
                    roots.Add(node);
                    return;
                }
                CollectSubtreeRoots(node.Left, depth + 1, roots);
                CollectSubtreeRoots(node.Right, depth + 1, roots);
            }

            public List<Tuple<int, int>> TraverseTreeToList(TrieNode root)
            {
                 // We need to produce List<(left, right)> ordered by NodeNum.
                 // Since we assigned NodeNum in BFS order, we can just BFS again (or store in array).
                 var list = new List<Tuple<int, int>>();
                 // We need to iterate in NodeNum order.
                 // We can collect all indexed nodes.
                 var nodes = new List<TrieNode>();
                 CollectIndexedNodes(root, 0, nodes);
                 nodes.Sort((a,b) => a.NodeNum.CompareTo(b.NodeNum));
                 
                 foreach(var n in nodes)
                 {
                     int l = (n.Left == null) ? 0x800000 : (n.Left.IsLeaf ? (0x800000 | n.Left.GeoIsp) : n.Left.NodeNum);
                     int r = (n.Right == null) ? 0x800000 : (n.Right.IsLeaf ? (0x800000 | n.Right.GeoIsp) : n.Right.NodeNum);
                     list.Add(Tuple.Create(l, r));
                 }
                 return list;
            }
            
            private void CollectIndexedNodes(TrieNode node, int depth, List<TrieNode> list)
            {
                if (node == null) return;
                if (depth >= 16 && !node.IsLeaf) list.Add(node);
                if (node.IsLeaf) return;
                
                CollectIndexedNodes(node.Left, depth + 1, list);
                CollectIndexedNodes(node.Right, depth + 1, list);
            }
        }
    }
}

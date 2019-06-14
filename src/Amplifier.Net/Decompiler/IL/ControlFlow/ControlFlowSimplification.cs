﻿// Copyright (c) 2014 Daniel Grunwald
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Amplifier.Decompiler.IL.Transforms;
using Amplifier.Decompiler.TypeSystem;

namespace Amplifier.Decompiler.IL.ControlFlow
{
	/// <summary>
	/// This transform 'optimizes' the control flow logic in the IL code:
	/// it replaces constructs that are generated by the C# compiler in debug mode
	/// with shorter constructs that are more straightforward to analyze.
	/// </summary>
	/// <remarks>
	/// The transformations performed are:
	/// * 'nop' instructions are removed
	/// * branches that lead to other branches are replaced with branches that directly jump to the destination
	/// * branches that lead to a 'return block' are replaced with a return instruction
	/// * basic blocks are combined where possible
	/// </remarks>
	public class ControlFlowSimplification : IILTransform
	{
		internal bool aggressivelyDuplicateReturnBlocks;

		public void Run(ILFunction function, ILTransformContext context)
		{
			foreach (var block in function.Descendants.OfType<Block>()) {
				context.CancellationToken.ThrowIfCancellationRequested();

				RemoveNopInstructions(block);

				InlineVariableInReturnBlock(block, context);
				// 1st pass SimplifySwitchInstruction before SimplifyBranchChains()
				// starts duplicating return instructions.
				SwitchDetection.SimplifySwitchInstruction(block);
			}
			SimplifyBranchChains(function, context);
			CleanUpEmptyBlocks(function, context);
		}

		private static void RemoveNopInstructions(Block block)
		{
			// Move ILRanges of special nop instructions to the previous non-nop instruction.
			for (int i = block.Instructions.Count - 1; i > 0; i--) {
				if (block.Instructions[i] is Nop nop && nop.Kind == NopKind.Pop) {
					block.Instructions[i - 1].AddILRange(nop);
				}
			}

			// Remove 'nop' instructions
			block.Instructions.RemoveAll(inst => inst.OpCode == OpCode.Nop);
		}

		void InlineVariableInReturnBlock(Block block, ILTransformContext context)
		{
			// In debug mode, the C#-compiler generates 'return blocks' that
			// unnecessarily store the return value to a local and then load it again:
			//   v = <inst>
			//   ret(v)
			// (where 'v' has no other uses)
			// Simplify these to a simple `ret(<inst>)` so that they match the release build version.
			// 
			if (block.Instructions.Count == 2 && block.Instructions[1].MatchReturn(out ILInstruction value)) {
				var ret = (Leave)block.Instructions[1];
				if (value.MatchLdLoc(out ILVariable v)
					&& v.IsSingleDefinition && v.LoadCount == 1 && block.Instructions[0].MatchStLoc(v, out ILInstruction inst)) {
					context.Step("Inline variable in return block", block);
					inst.AddILRange(ret.Value);
					inst.AddILRange(block.Instructions[0]);
					ret.Value = inst;
					block.Instructions.RemoveAt(0);
				}
			}
		}
		
		void SimplifyBranchChains(ILFunction function, ILTransformContext context)
		{
			List<(BlockContainer, Block)> blocksToAdd = new List<(BlockContainer, Block)>();
			HashSet<Block> visitedBlocks = new HashSet<Block>();
			foreach (var branch in function.Descendants.OfType<Branch>()) {
				// Resolve chained branches to the final target:
				var targetBlock = branch.TargetBlock;
				visitedBlocks.Clear();
				while (targetBlock.Instructions.Count == 1 && targetBlock.Instructions[0].OpCode == OpCode.Branch) {
					if (!visitedBlocks.Add(targetBlock)) {
						// prevent infinite loop when branch chain is cyclic
						break;
					}
					context.Step("Simplify branch to branch", branch);
					var nextBranch = (Branch)targetBlock.Instructions[0];
					branch.TargetBlock = nextBranch.TargetBlock;
					branch.AddILRange(nextBranch);
					if (targetBlock.IncomingEdgeCount == 0)
						targetBlock.Instructions.Clear(); // mark the block for deletion
					targetBlock = branch.TargetBlock;
				}
				if (IsBranchToReturnBlock(branch)) {
					if (aggressivelyDuplicateReturnBlocks) {
						// Replace branches to 'return blocks' with the return instruction
						context.Step("Replace branch to return with return", branch);
						branch.ReplaceWith(targetBlock.Instructions[0].Clone());
					} else if (branch.TargetContainer != branch.Ancestors.OfType<BlockContainer>().First()) {
						// We don't want to always inline the return directly, because this
						// might force us to place the return within a loop, when it's better
						// placed outside.
						// But we do want to move the return block into the correct try-finally scope,
						// so that loop detection at least has the option to put it inside
						// the loop body.
						context.Step("Copy return block into try block", branch);
						Block blockCopy = (Block)branch.TargetBlock.Clone();
						BlockContainer localContainer = branch.Ancestors.OfType<BlockContainer>().First();
						blocksToAdd.Add((localContainer, blockCopy));
						branch.TargetBlock = blockCopy;
					}
				} else if (targetBlock.Instructions.Count == 1 && targetBlock.Instructions[0] is Leave leave && leave.Value.MatchNop()) {
					context.Step("Replace branch to leave with leave", branch);
					// Replace branches to 'leave' instruction with the leave instruction
					var leave2 = leave.Clone();
					if (!branch.HasILRange) // use the ILRange of the branch if possible
						leave2.AddILRange(branch);
					branch.ReplaceWith(leave2);
				}
				if (targetBlock.IncomingEdgeCount == 0)
					targetBlock.Instructions.Clear(); // mark the block for deletion
			}
			foreach (var (container, block) in blocksToAdd) {
				container.Blocks.Add(block);
			}
		}
		
		void CleanUpEmptyBlocks(ILFunction function, ILTransformContext context)
		{
			foreach (var container in function.Descendants.OfType<BlockContainer>()) {
				foreach (var block in container.Blocks) {
					if (block.Instructions.Count == 0)
						continue; // block is already marked for deletion
					while (CombineBlockWithNextBlock(container, block, context)) {
						// repeat combining blocks until it is no longer possible
						// (this loop terminates because a block is deleted in every iteration)
					}
				}
				// Remove return blocks that are no longer reachable:
				container.Blocks.RemoveAll(b => b.IncomingEdgeCount == 0 && b.Instructions.Count == 0);
				if (context.Settings.RemoveDeadCode) {
					container.SortBlocks(deleteUnreachableBlocks: true);
				}
			}
		}

		bool IsBranchToReturnBlock(Branch branch)
		{
			var targetBlock = branch.TargetBlock;
			if (targetBlock.Instructions.Count != 1 || targetBlock.FinalInstruction.OpCode != OpCode.Nop)
				return false;
			return targetBlock.Instructions[0].MatchReturn(out var value) && value is LdLoc;
		}
		
		static bool CombineBlockWithNextBlock(BlockContainer container, Block block, ILTransformContext context)
		{
			Debug.Assert(container == block.Parent);
			// Ensure the block will stay a basic block -- we don't want extended basic blocks prior to LoopDetection.
			if (block.Instructions.Count > 1 && block.Instructions[block.Instructions.Count - 2].HasFlag(InstructionFlags.MayBranch))
				return false;
			Branch br = block.Instructions.Last() as Branch;
			// Check whether we can combine the target block with this block
			if (br == null || br.TargetBlock.Parent != container || br.TargetBlock.IncomingEdgeCount != 1)
				return false;
			if (br.TargetBlock == block)
				return false; // don't inline block into itself
			context.Step("CombineBlockWithNextBlock", br);
			var targetBlock = br.TargetBlock;
			if (targetBlock.StartILOffset < block.StartILOffset && IsDeadTrueStore(block)) {
				// The C# compiler generates a dead store for the condition of while (true) loops.
				block.Instructions.RemoveRange(block.Instructions.Count - 3, 2);
			}

			if (block.HasILRange)
				block.AddILRange(targetBlock);

			block.Instructions.Remove(br);
			block.Instructions.AddRange(targetBlock.Instructions);
			targetBlock.Instructions.Clear(); // mark targetBlock for deletion
			return true;
		}

		/// <summary>
		/// Returns true if the last two instructions before the branch are storing the value 'true' into an unused variable.
		/// </summary>
		private static bool IsDeadTrueStore(Block block)
		{
			if (block.Instructions.Count < 3) return false;
			if (!(block.Instructions.SecondToLastOrDefault() is StLoc deadStore && block.Instructions[block.Instructions.Count - 3] is StLoc tempStore))
				return false;
			if (!(deadStore.Variable.LoadCount == 0 && deadStore.Variable.AddressCount == 0))
				return false;
			if (!(deadStore.Value.MatchLdLoc(tempStore.Variable) && tempStore.Variable.IsSingleDefinition && tempStore.Variable.LoadCount == 1))
				return false;
			return tempStore.Value.MatchLdcI4(1) && deadStore.Variable.Type.IsKnownType(KnownTypeCode.Boolean);
		}
	}
}

// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Tests;

/// <summary>
/// Classes holding a timing-budget test (plan §5, task budgets). xUnit runs a collection with parallelization disabled after the
/// parallel ones, alone, so a budget measures the code and not the CPU contention from the rest of the suite. The budgets
/// themselves are never relaxed here.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BudgetCollection
{
    public const string Name = "budgets";
}

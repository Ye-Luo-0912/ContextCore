import sys
path = 'src/ContextCore.ControlRoom/Commands/EvalCommand.cs'
with open(path, 'r', encoding='utf-8') as f:
    lines = f.readlines()

# Helper: find exact line index for a pattern
def find_line(pat, start=0):
    for i in range(start, len(lines)):
        if pat in lines[i]:
            return i
    return -1

# 1. Validation list - add after mainline-shadow-package-comparison
idx = find_line("vector-mainline-shadow-adapter-package-comparison-gate") + 1
# skip to the && line end
while idx < len(lines) and '&&' not in lines[idx]:
    idx += 1
lines[idx] = lines[idx].rstrip() + '\n            !string.Equals(subcommand, \"vector-allowlisted-mainline-shadow-adapter-observation\", StringComparison.OrdinalIgnoreCase) &&\n            !string.Equals(subcommand, \"vector-allowlisted-mainline-shadow-adapter-observation-gate\", StringComparison.OrdinalIgnoreCase) &&\n'

# 2. Help text
idx = find_line('vector-mainline-shadow-adapter-package-comparison')
if idx >= 0:
    lines[idx] = lines[idx].rstrip() + '\n            Console.WriteLine(\"  eval vector-allowlisted-mainline-shadow-adapter-observation\");\n            Console.WriteLine(\"  eval vector-allowlisted-mainline-shadow-adapter-observation-gate\");\n'

# 3. Dispatch branch - after mainline dispatch
idx = find_line('ExecuteMainlineShadowAdapterPackageComparisonAsync') + 1
while idx < len(lines) and 'return' not in lines[idx]:
    idx += 1
# skip past return;
lines[idx] += '\n        }\n\n        if (string.Equals(subcommand, "vector-allowlisted-mainline-shadow-adapter-observation", StringComparison.OrdinalIgnoreCase)\n            || string.Equals(subcommand, "vector-allowlisted-mainline-shadow-adapter-observation-gate", StringComparison.OrdinalIgnoreCase))\n        {\n            await ExecuteAllowlistedMainlineShadowAdapterObservationAsync(args, subcommand, cancellationToken).ConfigureAwait(false);\n            return;'

# Write back
with open(path, 'w', encoding='utf-8') as f:
    f.writelines(lines)
print('done')
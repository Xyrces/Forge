import re

with open('Program.cs', 'r') as f:
    content = f.read()

# Fix 1: Remove the misplaced line from ProcessExit block
content = content.replace(
    "            gateOptions: options.Gates,\n        };\n\ntry",
    "        };\n\ntry"
)

# Fix 2: Add it before the closing paren of the DashboardHost ctor call
# Find the second occurrence of DashboardHost constructor - the orchestrator one
# It ends with "lifecycle: lifecycle);"
content = content.replace(
    "            lifecycle: lifecycle);",
    "            lifecycle: lifecycle,\n            gateOptions: options.Gates);"
)

with open('Program.cs', 'w') as f:
    f.write(content)

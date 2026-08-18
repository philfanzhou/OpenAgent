import json
import sys

left = int(sys.argv[1])
right = int(sys.argv[2])
print(json.dumps({"sum": left + right, "source": "isolated-skill-python"}))

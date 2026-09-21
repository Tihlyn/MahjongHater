"""Prints the torch / numpy versions and the CUDA device Setup-Training.ps1 found."""
import numpy
import torch

print("torch", torch.__version__, "numpy", numpy.__version__)
if torch.cuda.is_available():
    p = torch.cuda.get_device_properties(0)
    print("cuda", torch.version.cuda, "device", p.name, "%.1f GB" % (p.total_memory / 2 ** 30))
else:
    print("cuda NOT available: training will run on the CPU (pass -Device cpu to Run-Training.ps1; expect ~10x longer)")

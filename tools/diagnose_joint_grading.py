"""Independent, optional developer check; never used by Godot at runtime.

Read --dump-joint-model output and ask HiGHS whether the *same* linear
constraints are feasible. Run with .tools/grade_solver/Scripts/python.exe.
"""
import json
from pathlib import Path
import numpy as np
from scipy.optimize import linprog
from scipy.sparse import coo_matrix, vstack, hstack, csr_matrix

root = Path(__file__).resolve().parents[1]
model = json.loads((root / "artifacts/joint_vertical_model.json").read_text())
rows = model["rows"]
ii, jj, vv = [], [], []
for i, row in enumerate(rows):
    for name, coefficient in zip(("A", "B", "C", "D"), ("U", "V", "W", "Q")):
        if row[name] >= 0:
            ii.append(i); jj.append(row[name]); vv.append(row[coefficient])
a = coo_matrix((vv, (ii, jj)), shape=(len(rows), model["count"])).tocsr()
lower = np.array([r["Low"] for r in rows]); upper = np.array([r["High"] for r in rows])
matrix = vstack((a, -a)).tocsr(); limits = np.concatenate((upper, -lower))
options = {"primal_feasibility_tolerance": 1e-9, "dual_feasibility_tolerance": 1e-9}
result = linprog(np.zeros(model["count"]), A_ub=matrix, b_ub=limits,
                 bounds=(None, None), method="highs", options=options)
report = {"feasible": bool(result.success), "status": int(result.status), "message": result.message}
if result.success:
    error = np.maximum(lower - a @ result.x, a @ result.x - upper)
    report["max_world_violation"] = float(np.max(error / np.array([r["Scale"] for r in rows])))
else:
    scale = np.array([r["Scale"] for r in rows])
    extended = hstack((matrix, csr_matrix(-np.concatenate((scale, scale))[:, None]))).tocsr()
    costs = np.zeros(model["count"] + 1); costs[-1] = 1
    relaxed = linprog(costs, A_ub=extended, b_ub=limits,
                      bounds=[(None, None)] * model["count"] + [(0, None)], method="highs", options=options)
    if relaxed.success:
        report["minimum_common_world_residual"] = float(relaxed.x[-1])
        error = np.maximum(lower - a @ relaxed.x[:-1], a @ relaxed.x[:-1] - upper) / scale
        worst = np.argsort(error)[-16:][::-1]
        report["conflicting_rows"] = [dict(rows[i], residual=float(error[i])) for i in worst]
print(json.dumps(report, indent=2))
(root / "artifacts/joint_vertical_feasibility.json").write_text(json.dumps(report, indent=2))

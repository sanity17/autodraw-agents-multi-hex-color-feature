using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Autodraw.Painting.Calibration;

namespace Autodraw.Painting.ColorInput;

public interface IColorInputMethod
{
    string Name { get; }
    bool IsCalibrated { get; }
    Task ApplyColor(string hex, CancellationToken ct);
    Task CalibrateAsync(IEnumerable<Vector2> points);
    void Save(PickerProfile profile);
    void Load(PickerProfile profile);
}

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Autodraw.Painting.Calibration;

namespace Autodraw.Painting.ColorInput;

public class HexFieldInput : IColorInputMethod
{
    public string Name => "Hex";
    public bool IsCalibrated => false;
    public Task ApplyColor(string hex, CancellationToken ct) => throw new NotImplementedException();
    public Task CalibrateAsync(IEnumerable<Vector2> points) => Task.CompletedTask;
    public void Save(PickerProfile profile) { }
    public void Load(PickerProfile profile) { }
}

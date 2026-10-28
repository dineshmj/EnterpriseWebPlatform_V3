using System.Diagnostics.Metrics;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Support;

/// <summary>Meters for a test (the host's IMeterFactory is not there); disposed with the factory.</summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (var meter in _meters)
            meter.Dispose();
        _meters.Clear();
    }
}
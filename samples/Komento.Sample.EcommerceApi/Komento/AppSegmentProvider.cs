using Komento.Sample.EcommerceApi.Infrastructure;

namespace Komento.Sample.EcommerceApi.Komento;

internal sealed class AppSegmentProvider(VipBinSetStore vipStore, NatsLoyaltyStore loyaltyStore) : global::Komento.ISegmentProvider
{
    public ValueTask<bool> IsInSegmentAsync(string subjectId, string segmentName, CancellationToken ct = default)
        => segmentName switch
        {
            "vip"     => ValueTask.FromResult(vipStore.Contains(subjectId)),
            "loyalty" => loyaltyStore.IsMemberAsync(subjectId, ct),
            _         => ValueTask.FromResult(false)
        };
}

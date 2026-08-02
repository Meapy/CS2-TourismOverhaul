using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TourismOverhaul.Components
{
    /// <summary>
    /// Marks a tourist household that has been sent shopping and has not yet paid.
    ///
    /// The ledger used to recognise shopping by finding ResourceBuyer on a citizen, but that
    /// component exists only while a purchase is outstanding. Households are sampled every few
    /// thousand frames, so a trip that started and settled in between left no trace, the money went
    /// to the unattributed bucket, and shops read 1% of tourist spending against 24% unattributed.
    ///
    /// TouristShoppingSystem is what grants the need in the first place, so it can say so directly
    /// rather than leaving the ledger to catch a fleeting state. The mark is cleared by the drop it
    /// explains.
    ///
    /// IEmptySerializable rather than an empty ISerializable: the latter throws
    /// ComponentSerializerException with "Nothing serialized for component" on load.
    /// </summary>
    public struct ExpectsPurchase : IComponentData, IEmptySerializable
    {
    }
}

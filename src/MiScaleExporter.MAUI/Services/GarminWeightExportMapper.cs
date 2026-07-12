using MiScaleExporter.Models;
using YetAnotherGarminConnectClient.Dto.Garmin.Fit;

namespace MiScaleExporter.Services;

public static class GarminWeightExportMapper
{
    public static GarminWeightExportData Map(BodyComposition bodyComposition, DateTime time)
    {
        ArgumentNullException.ThrowIfNull(bodyComposition);

        var muscleMass = bodyComposition.SkeletalMuscleMass ?? bodyComposition.MuscleMass;

        return new GarminWeightExportData
        {
            TimeStamp = time,
            Weight = Convert.ToSingle(bodyComposition.Weight),
            PercentFat = Convert.ToSingle(bodyComposition.Fat),
            PercentHydration = Convert.ToSingle(bodyComposition.WaterPercentage),
            BoneMass = Convert.ToSingle(bodyComposition.BoneMass),
            MuscleMass = Convert.ToSingle(muscleMass),
            VisceralFatRating = Convert.ToByte(bodyComposition.VisceralFat),
            VisceralFatMass = Convert.ToSingle(bodyComposition.VisceralFat),
            PhysiqueRating = Convert.ToByte(bodyComposition.BodyType),
            MetabolicAge = Convert.ToByte(bodyComposition.MetabolicAge),
            BodyMassIndex = Convert.ToSingle(bodyComposition.BMI),
            BasalMet = IsPositiveFinite(bodyComposition.BMR)
                ? Convert.ToSingle(bodyComposition.BMR)
                : null,
        };
    }

    public static GarminWeightScaleDTO ToGarminDto(GarminWeightExportData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var dto = new GarminWeightScaleDTO
        {
            TimeStamp = data.TimeStamp,
            Weight = data.Weight,
            PercentFat = data.PercentFat,
            PercentHydration = data.PercentHydration,
            BoneMass = data.BoneMass,
            MuscleMass = data.MuscleMass,
            VisceralFatRating = data.VisceralFatRating,
            VisceralFatMass = data.VisceralFatMass,
            PhysiqueRating = data.PhysiqueRating,
            MetabolicAge = data.MetabolicAge,
            BodyMassIndex = data.BodyMassIndex,
            BasalMet = data.BasalMet,
        };
        return dto;
    }

    public static GarminBodyCompositionRequest ToProxyRequest(
        GarminWeightExportData data,
        CredentialsData credentials)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(credentials);

        return new GarminBodyCompositionRequest
        {
            Email = credentials.Email,
            Password = credentials.Password,
            AccessToken = credentials.AccessToken,
            TokenSecret = credentials.TokenSecret,
            Weight = data.Weight,
            BoneMass = data.BoneMass,
            MuscleMass = data.MuscleMass,
            MetabolicAge = data.MetabolicAge,
            PercentFat = data.PercentFat,
            VisceralFatRating = data.VisceralFatRating,
            BodyMassIndex = data.BodyMassIndex,
            PercentHydration = data.PercentHydration,
            PhysiqueRating = data.PhysiqueRating,
            BasalMet = data.BasalMet,
            TimeStamp = ((DateTimeOffset)data.TimeStamp).ToUnixTimeSeconds(),
        };
    }

    private static bool IsPositiveFinite(double value) => value > 0 && double.IsFinite(value);
}

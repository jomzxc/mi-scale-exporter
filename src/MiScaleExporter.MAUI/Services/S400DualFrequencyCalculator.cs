using System;
using System.Collections.Generic;

namespace MiScaleExporter.Services
{
    public enum Gender
    {
        Male,
        Female
    }

    public sealed class S400Input
    {
        public double WeightKg { get; set; }
        public int AgeYears { get; set; }
        public double HeightCm { get; set; }
        public Gender Gender { get; set; }

        // Raw S400 dual-frequency values from parser.
        // IMPORTANT: naming in BLE integrations may be inverted by frequency.
        public double ImpedanceLowRaw { get; set; }
        public double ImpedanceHighRaw { get; set; }
    }

    public sealed class S400Output
    {
        public double BMI { get; set; }
        public double LBM { get; set; }
        public double FatPercentage { get; set; }
        public double WaterPercentage { get; set; }
        public double BoneMassKg { get; set; }
        public double MuscleMassKg { get; set; }
        public double SkeletalMuscleMassKg { get; set; }
        public double BMR { get; set; }
        public double VisceralFat { get; set; }
        public double MetabolicAge { get; set; }
        public double ProteinPercentage { get; set; }
        public double FatMassToIdealWeightKg { get; set; }
        public string BodyType { get; set; } = "balanced";

        // S400 dual extras
        public double ECWLiters { get; set; }
        public double ICWLiters { get; set; }
        public double ECWTBWPercentage { get; set; }
        public double BCMKg { get; set; }

        // Optional body score parity
        public double BodyScore { get; set; }

        // Debug / trace
        public double Zlf { get; set; } // 50kHz effective
        public double Zhf { get; set; } // 250kHz effective
    }

    public static class S400DualFrequencyCalculator
    {
        public static S400Output Calculate(S400Input i)
        {
            var o = new S400Output();

            if (i == null || i.WeightKg <= 0 || i.HeightCm <= 0 || i.AgeYears <= 0)
                return o;

            var zlf = GetZlf(i.ImpedanceLowRaw, i.ImpedanceHighRaw);
            var zhf = GetZhf(i.ImpedanceLowRaw, i.ImpedanceHighRaw);

            o.Zlf = zlf;
            o.Zhf = zhf;

            o.BMI = Clamp(i.WeightKg / Math.Pow(i.HeightCm / 100.0, 2), 10, 90);

            // LBM (hardware-calibrated formula)
            o.LBM = GetLbm(i.HeightCm, i.WeightKg, i.AgeYears, zlf);

            // S400 fat% uses 2-compartment simplification in current bodymiscale logic
            o.FatPercentage = Clamp(((i.WeightKg - o.LBM) / i.WeightKg) * 100.0, 5, 75);

            // S400 displayed water% uses Pace constant
            o.WaterPercentage = ClampWater((100.0 - o.FatPercentage) * 0.73);

            // ECW/ICW/BCM use fat%-derived TBW liters
            var tbwLiters = GetTbwForCompartments(i.WeightKg, o.FatPercentage);
            o.ECWLiters = GetEcw(tbwLiters, zlf, zhf);
            o.ICWLiters = Math.Max(0.0, tbwLiters - o.ECWLiters);
            o.ECWTBWPercentage = tbwLiters > 0 ? (o.ECWLiters / tbwLiters) * 100.0 : 0.0;
            o.BCMKg = o.ICWLiters > 0 ? o.ICWLiters / 0.73 : 0.0;

            // SMM (Janssen 2000 with Zlf)
            o.SkeletalMuscleMassKg = GetSkeletalMuscleMass(i.HeightCm, i.AgeYears, i.Gender, zlf);

            // Bone / muscle
            o.BoneMassKg = GetBoneMass(o.LBM, i.Gender);
            o.MuscleMassKg = GetMuscleMass(i.WeightKg, o.FatPercentage, o.BoneMassKg, i.Gender);

            // BMR S400 = Katch-McArdle
            o.BMR = Clamp(370.0 + 21.6 * o.LBM, 500, 5000);

            // Visceral = Zepp common formula
            o.VisceralFat = GetVisceralFat(i.HeightCm, i.WeightKg, i.AgeYears, i.Gender);

            // Metabolic age S400 = BMR-relative
            o.MetabolicAge = GetMetabolicAgeS400(i.HeightCm, i.WeightKg, i.AgeYears, i.Gender, o.LBM);

            // Protein% S400 = Wang 1999 fraction of LBM
            o.ProteinPercentage = Clamp((o.LBM * 0.195 / i.WeightKg) * 100.0, 5, 32);

            // Fat mass to ideal weight and body type need scale tables
            var scale = new Scale(i.HeightCm, i.Gender);
            var fatScale = scale.GetFatPercentage(i.AgeYears);
            o.FatMassToIdealWeightKg = i.WeightKg * (fatScale[2] / 100.0) - i.WeightKg * (o.FatPercentage / 100.0);
            o.BodyType = GetBodyType(o.FatPercentage, o.MuscleMassKg, fatScale, scale.MuscleMass);

            // Optional body score parity
            o.BodyScore = GetBodyScore(i, o, scale);

            return o;
        }

        // ---- impedance helpers ----
        private static double GetZlf(double z1, double z2)
            => (z1 > 0 && z2 > 0) ? Math.Max(z1, z2) : z1;

        private static double GetZhf(double z1, double z2)
            => (z1 > 0 && z2 > 0) ? Math.Min(z1, z2) : z2;

        // ---- core formulas ----
        private static double GetLbm(double hCm, double wKg, int age, double z)
        {
            if (hCm <= 0 || wKg <= 0 || z <= 0) return 0.0;
            var lbm = (hCm * 9.058 / 100.0) * (hCm / 100.0) + wKg * 0.32 + 12.226 - z * 0.0068 - age * 0.0542;
            return Math.Min(lbm, wKg * 0.98);
        }

        private static double GetTbwForCompartments(double wKg, double fatPct)
            => wKg > 0 ? (1.0 - fatPct / 100.0) * 0.73 * wKg : 0.0;

        private static double GetEcw(double tbwLiters, double zlf, double zhf)
        {
            if (tbwLiters <= 0 || zlf <= 0 || zhf <= 0) return 0.0;
            var ratio = zhf / zlf;
            return tbwLiters * (0.32 + 0.08 * ratio);
        }

        private static double GetSkeletalMuscleMass(double hCm, int age, Gender gender, double zlf)
        {
            if (hCm <= 0 || age <= 0 || zlf <= 0) return 0.0;
            var ri = (hCm * hCm) / zlf;
            var sex = gender == Gender.Male ? 1.0 : 0.0;
            return Math.Max(0.0, (ri * 0.401) + (sex * 3.825) + (age * -0.071) + 5.102);
        }

        private static double GetBoneMass(double lbm, Gender gender)
        {
            var baseVal = gender == Gender.Female ? 0.245691014 : 0.18016894;
            var bone = (baseVal - (lbm * 0.05158)) * -1.0;
            bone += bone > 2.2 ? 0.1 : -0.1;
            if ((gender == Gender.Female && bone > 5.1) || (gender == Gender.Male && bone > 5.2))
                bone = 8.0;
            return Clamp(bone, 0.5, 8);
        }

        private static double GetMuscleMass(double wKg, double fatPct, double boneKg, Gender gender)
        {
            var m = wKg - (fatPct * 0.01 * wKg) - boneKg;
            if ((gender == Gender.Female && m >= 84) || (gender == Gender.Male && m >= 93.5))
                m = 120.0;
            return Clamp(m, 10, 120);
        }

        private static double GetVisceralFat(double hCm, double wKg, int age, Gender gender)
        {
            double v;
            if (gender == Gender.Male)
            {
                if (hCm < wKg * 1.6 + 63.0)
                    v = age * 0.15 + ((wKg * 305.0) / ((hCm * 0.0826 * hCm - hCm * 0.4) + 48.0) - 2.9);
                else
                    v = age * 0.15 + (wKg * (hCm * -0.0015 + 0.765) - hCm * 0.143) - 5.0;
            }
            else
            {
                if (wKg <= hCm * 0.5 - 13.0)
                    v = age * 0.07 + (wKg * (hCm * -0.0024 + 0.691) - hCm * 0.027) - 10.5;
                else
                    v = age * 0.07 + ((wKg * 500.0) / ((hCm * 1.45 + hCm * 0.1158 * hCm) - 120.0) - 6.0);
            }
            return Clamp(v, 1, 50);
        }

        private static double GetMetabolicAgeS400(double hCm, double wKg, int age, Gender gender, double lbm)
        {
            var bmrActual = lbm > 0 ? 370 + 21.6 * lbm : 0.0;
            if (bmrActual <= 0) return age;

            var bmrExpected = gender == Gender.Male
                ? 88.362 + 13.397 * wKg + 4.799 * hCm - 5.677 * age
                : 447.593 + 9.247 * wKg + 3.098 * hCm - 4.330 * age;

            var raw = age * (bmrExpected / bmrActual);
            return GetMetabolicAgeClamped((int)Math.Round(raw), age);
        }

        // ---- body type ----
        private static string GetBodyType(double fatPct, double muscle, List<double> fatScale, List<double> muscleScale)
        {
            var factor = fatPct > fatScale[2] ? 0 : (fatPct < fatScale[1] ? 2 : 1);
            var mFactor = muscle > muscleScale[1] ? 2 : (muscle < muscleScale[0] ? 0 : 1);
            var idx = mFactor + (factor * 3);

            var labels = new[]
            {
                "obese","overweight","thick_set",
                "lack_exercise","balanced","balanced_muscular",
                "skinny","balanced_skinny","skinny_muscular"
            };
            return labels[Math.Clamp(idx, 0, labels.Length - 1)];
        }

        // ---- body score parity (from pasted module) ----
        private static double GetBodyScore(S400Input i, S400Output o, Scale scale)
        {
            double score = 100.0;

            score -= CalcBmiDeduct(i, o, scale);
            score -= CalcBodyFatDeduct(i, o, scale);
            score -= CalcMuscleDeductS400(o, scale);
            score -= CalcWaterDeduct(i.Gender, o.WaterPercentage);
            score -= CalcVisceralDeduct(o.VisceralFat);
            score -= CalcBoneDeduct(i.Gender, i.WeightKg, o.BoneMassKg);
            score -= CalcBmrDeduct(i.Gender, i.AgeYears, i.WeightKg, o.BMR);
            score -= CalcProteinDeduct(o.ProteinPercentage);

            return Clamp(score, 10, 100);
        }

        private static double CalcBmiDeduct(S400Input i, S400Output o, Scale scale)
        {
            const double veryLow = 14, low = 15, normal = 18.5, overweight = 28, obese = 32;
            if (i.HeightCm < 90) return 0.0;

            var fatScale = scale.GetFatPercentage(i.AgeYears);

            if (o.BMI <= veryLow) return 30.0;

            if ((o.FatPercentage < fatScale[2]) &&
                ((o.BMI >= normal && i.AgeYears >= 18) || (o.BMI >= low && i.AgeYears < 18)))
                return 0.0;

            if (o.BMI < low) return GetMalus(o.BMI, veryLow, low, 30, 15) + 15.0;
            if (o.BMI < normal && i.AgeYears >= 18) return GetMalus(o.BMI, 15.0, 18.5, 15, 5) + 5.0;

            if (o.FatPercentage >= fatScale[2])
            {
                if (o.BMI >= obese) return 10.0;
                if (o.BMI > overweight) return GetMalus(o.BMI, 28.0, 25.0, 5, 10) + 5.0;
            }

            return 0.0;
        }

        private static double CalcBodyFatDeduct(S400Input i, S400Output o, Scale scale)
        {
            var fs = scale.GetFatPercentage(i.AgeYears);
            var best = i.Gender == Gender.Male ? fs[2] - 3.0 : fs[2] - 2.0;

            if (fs[0] <= o.FatPercentage && o.FatPercentage < best) return 0.0;
            if (o.FatPercentage >= fs[3]) return 20.0;
            if (o.FatPercentage < fs[3]) return GetMalus(o.FatPercentage, fs[3], fs[2], 20, 10) + 10.0;
            if (o.FatPercentage <= fs[2]) return GetMalus(o.FatPercentage, fs[2], best, 3, 9) + 3.0;
            if (o.FatPercentage < fs[0]) return GetMalus(o.FatPercentage, 1.0, fs[0], 3, 10) + 3.0;
            return 0.0;
        }

        private static double CalcMuscleDeductS400(S400Output o, Scale scale)
        {
            var smm = o.SkeletalMuscleMassKg;
            if (smm <= 0) return 0.0;

            var targetMin = (scale.MuscleMass[0] - 5.0) * 0.77;
            var targetMax = scale.MuscleMass[0] * 0.77;
            return CalcCommonDeduct(targetMin, targetMax, smm);
        }

        private static double CalcWaterDeduct(Gender g, double waterPct)
        {
            var normal = g == Gender.Male ? 55.0 : 45.0;
            return CalcCommonDeduct(normal - 5.0, normal, waterPct);
        }

        private static double CalcBoneDeduct(Gender g, double weight, double boneMass)
        {
            (double minWeight, double boneMass)[] entries = g == Gender.Male
                ? new[] { (75d, 2.0d), (60d, 1.9d), (0d, 1.6d) }
                : new[] { (60d, 1.8d), (45d, 1.5d), (0d, 1.3d) };

            var expected = entries[^1].boneMass;
            foreach (var e in entries)
            {
                if (weight >= e.minWeight) { expected = e.boneMass; break; }
            }
            return CalcCommonDeduct(expected - 0.3, expected, boneMass);
        }

        private static double CalcVisceralDeduct(double visceral)
        {
            const double maxData = 15.0, minData = 10.0;
            if (visceral < minData) return 0.0;
            if (visceral >= maxData) return 15.0;
            return GetMalus(visceral, maxData, minData, maxData, minData) + 10.0;
        }

        private static double CalcBmrDeduct(Gender g, int age, double weight, double bmr)
        {
            double normal = 20.0;
            var coeffs = g == Gender.Male
                ? new[] { (30, 21.6), (50, 20.07), (100, 19.35) }
                : new[] { (30, 21.24), (50, 19.53), (100, 18.63) };

            foreach (var (cAge, c) in coeffs)
            {
                if (age < cAge) { normal = weight * c; break; }
            }

            if (bmr >= normal) return 0.0;
            if (bmr <= normal - 300) return 6.0;
            return GetMalus(bmr, normal - 300, normal, 6, 3) + 5.0;
        }

        private static double CalcProteinDeduct(double protein)
        {
            if (protein > 17.0) return 0.0;
            if (protein < 10.0) return 10.0;
            if (protein <= 16.0) return GetMalus(protein, 10.0, 16.0, 10, 5) + 5.0;
            if (protein <= 17.0) return GetMalus(protein, 16.0, 17.0, 5, 3) + 3.0;
            return 0.0;
        }

        private static double CalcCommonDeduct(double min, double max, double value)
        {
            if (value >= max) return 0.0;
            if (value < min) return 10.0;
            return GetMalus(value, min, max, 10, 5) + 5.0;
        }

        private static double GetMalus(double data, double minData, double maxData, double maxMalus, double minMalus)
        {
            var den = (minData - maxData);
            if (Math.Abs(den) < 1e-12) return 0.0;
            var result = ((data - maxData) / den) * (maxMalus - minMalus);
            return Math.Max(0.0, result);
        }

        private static int GetMetabolicAgeClamped(int metabolicAge, int realAge)
        {
            if (metabolicAge <= 0) return Math.Max(15, realAge);
            var min = Math.Max(15, realAge - 15);
            var max = Math.Min(80, realAge + 15);
            return Math.Clamp(metabolicAge, min, max);
        }

        private static double ClampWater(double v) => Clamp(v, 35, 75);
        private static double Clamp(double v, double min, double max) => Math.Min(max, Math.Max(min, v));
    }

    // Bodymiscale Scale tables
    public sealed class Scale
    {
        private readonly double _height;
        private readonly Gender _gender;

        public Scale(double heightCm, Gender gender)
        {
            _height = heightCm;
            _gender = gender;
        }

        public List<double> GetFatPercentage(int age)
        {
            var rows = new List<(int min, int max, List<double> female, List<double> male)>
            {
                (0,12,new(){12,21,30,34},new(){7,16,25,30}),
                (12,14,new(){15,24,33,37},new(){7,16,25,30}),
                (14,16,new(){18,27,36,40},new(){7,16,25,30}),
                (16,18,new(){20,28,37,41},new(){7,16,25,30}),
                (18,40,new(){21,28,35,40},new(){11,17,22,27}),
                (40,60,new(){22,29,36,41},new(){12,18,23,28}),
                (60,101,new(){23,30,37,42},new(){14,20,25,30}),
            };

            foreach (var r in rows)
                if (age >= r.min && age < r.max)
                    return _gender == Gender.Female ? r.female : r.male;

            var last = rows[^1];
            return _gender == Gender.Female ? last.female : last.male;
        }

        public List<double> MuscleMass
        {
            get
            {
                var rows = new List<(Dictionary<Gender, int> minH, List<double> female, List<double> male)>
                {
                    (new(){{Gender.Male,170},{Gender.Female,160}}, new(){36.5,42.6}, new(){49.4,59.5}),
                    (new(){{Gender.Male,160},{Gender.Female,150}}, new(){32.9,37.6}, new(){44.0,52.5}),
                    (new(){{Gender.Male,0},{Gender.Female,0}},     new(){29.1,34.8}, new(){38.5,46.6}),
                };

                foreach (var r in rows)
                    if (_height >= r.minH[_gender])
                        return _gender == Gender.Female ? r.female : r.male;

                var last = rows[^1];
                return _gender == Gender.Female ? last.female : last.male;
            }
        }
    }
}

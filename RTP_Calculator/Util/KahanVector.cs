namespace RTP_Calculator.Util;

/// <summary>Compensated (Kahan-Babuska) summation of a vector of metrics.</summary>
public sealed class KahanVector
{
    private readonly double[] sum;
    private readonly double[] compensation;

    public KahanVector(int length) { sum = new double[length]; compensation = new double[length]; }

    public void Add(double[] values)
    {
        for (int i = 0; i < sum.Length; i++)
        {
            double v = values[i];
            double t = sum[i] + v;
            if (Math.Abs(sum[i]) >= Math.Abs(v)) compensation[i] += (sum[i] - t) + v;
            else compensation[i] += (v - t) + sum[i];
            sum[i] = t;
        }
    }

    public double[] Values
    {
        get
        {
            var result = new double[sum.Length];
            for (int i = 0; i < sum.Length; i++) result[i] = sum[i] + compensation[i];
            return result;
        }
    }
}

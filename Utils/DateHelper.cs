using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Adminbot.Utils
{
    static class DateHelper
    {
        public static string ConvertToHijriShamsi(this DateTime GregorianDate)
        {
            DateTime d = GregorianDate;
            PersianCalendar pc = new PersianCalendar();
            return string.Format("{0}/{1}/{2} - {3}:{4}", pc.GetYear(d), pc.GetMonth(d), pc.GetDayOfMonth(d), GregorianDate.Hour, GregorianDate.Minute);
            //Console.WriteLine(string.Format("{0}/{1}/{2}", pc.GetYear(d), pc.GetMonth(d), pc.GetDayOfMonth(d)));
        }
        public static string FormatCurrency(this long value)
        {
            CultureInfo cultureInfo = new CultureInfo("fa-IR"); // Persian (Iran) culture
            return value.ToString("N0", cultureInfo) + " تومان";
        }
    }
}
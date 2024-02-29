using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Drawing.Printing;
using System.Linq;
using System.Threading.Tasks;

namespace Adminbot.Domain
{

    public class CredUser
    {
        //public int Id { get; set; }
        [Key]
        public long TelegramUserId { get; set; } // Telegram user ID
        public long ChatID { get; set; }
        public string Username { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string LanguageCode { get; set; }
        public long AccountBalance { get; set; } = 0L;
        public string PhoneNumber { get; set; }
        public string Email { get; set; }
        public bool IsColleague { get; set; } = false;

        public override string ToString()
        {
            string val = "\u200F";
            if (!string.IsNullOrEmpty(FirstName))
                val += FirstName;
            if (!string.IsNullOrEmpty(LastName))
                val += " " + LastName;
            if (!string.IsNullOrEmpty(Username))
                val += " - @" + Username;
            if (IsColleague)
                val += " - " + "همکار ";
            else
                val += " - " + "عادی ";

            return val;
        }
    }

}
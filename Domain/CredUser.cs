using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace Adminbot.Domain
{

    public class CredUser
    {
        [Key]
        public int Id { get; set; }
        public long TelegramUserId { get; set; } // Telegram user ID
        public long ChatID { get; set; }
        public string Username { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string LanguageCode { get; set; }
        public decimal AccountBalance { get; set; } = 0;
        public string PhoneNumber { get; set; }
        public string Email { get; set; }
    }

}
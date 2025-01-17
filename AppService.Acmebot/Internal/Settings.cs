﻿using Microsoft.Extensions.Configuration;

namespace AppService.Acmebot.Internal
{
    internal class Settings
    {
        public Settings()
        {
            var configuration = new ConfigurationBuilder()
                                .AddJsonFile("local.settings.json", true)
                                .AddEnvironmentVariables()
                                .Build();

            _section = configuration.GetSection("LetsEncrypt");
        }

        private readonly IConfiguration _section;

        public string Contacts => _section[nameof(Contacts)];

        public string SubscriptionId => _section[nameof(SubscriptionId)];

        public string Webhook => _section[nameof(Webhook)];

        public static Settings Default { get; } = new Settings();
    }
}

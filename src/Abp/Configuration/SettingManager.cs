﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.Caching;
using Abp.Collections.Extensions;
using Abp.Dependency;
using Abp.Domain.Uow;
using Abp.Runtime.Caching;
using Abp.Runtime.Session;

namespace Abp.Configuration
{
    /// <summary>
    /// This class implements <see cref="ISettingManager"/> to manage setting values in the database.
    /// </summary>
    public class SettingManager : ISettingManager, ISingletonDependency
    {
        /// <summary>
        /// Reference to the current Session.
        /// </summary>
        public IAbpSession Session { get; set; }

        /// <summary>
        /// Reference to the setting store.
        /// </summary>
        public ISettingStore SettingStore { get; set; }

        private readonly ISettingDefinitionManager _settingDefinitionManager;

        private readonly Lazy<Dictionary<string, SettingInfo>> _applicationSettings;

        private readonly IThreadSafeCache<Dictionary<string, SettingInfo>> _tenantSettingCache;

        private readonly IThreadSafeCache<Dictionary<string, SettingInfo>> _userSettingCache;

        /// <inheritdoc/>
        public SettingManager(ISettingDefinitionManager settingDefinitionManager, IThreadSafeCacheFactoryService threadSafeCacheFactory)
        {
            _settingDefinitionManager = settingDefinitionManager;

            Session = NullAbpSession.Instance;
            SettingStore = NullSettingStore.Instance; //Should be constructor injection? For that, ISettingStore must be registered!

            _applicationSettings = new Lazy<Dictionary<string, SettingInfo>>(GetApplicationSettingsFromDatabase, true);
            _tenantSettingCache = threadSafeCacheFactory.CreateThreadSafeObjectCache<Dictionary<string, SettingInfo>>(GetType().FullName + ".TenantSettings", TimeSpan.FromMinutes(60)); //TODO: Get constant from somewhere else.
            _userSettingCache = threadSafeCacheFactory.CreateThreadSafeObjectCache<Dictionary<string, SettingInfo>>(GetType().FullName + ".UserSettings", TimeSpan.FromMinutes(20)); //TODO: Get constant from somewhere else.
        }

        #region Public methods

        /// <inheritdoc/>
        public string GetSettingValue(string name)
        {
            var settingDefinition = _settingDefinitionManager.GetSettingDefinition(name);

            //Get for user if defined
            if (settingDefinition.Scopes.HasFlag(SettingScopes.User) && Session.UserId.HasValue)
            {
                var settingValue = GetSettingValueForUserOrNull(Session.UserId.Value, name);
                if (settingValue != null)
                {
                    return settingValue.Value;
                }
            }

            //Get for tenant if defined
            if (settingDefinition.Scopes.HasFlag(SettingScopes.Tenant) && Session.TenantId.HasValue)
            {
                var settingValue = GetSettingValueForTenantOrNull(Session.TenantId.Value, name);
                if (settingValue != null)
                {
                    return settingValue.Value;
                }
            }

            //Get for application if defined
            if (settingDefinition.Scopes.HasFlag(SettingScopes.Application))
            {
                var settingValue = GetSettingValueForApplicationOrNull(name);
                if (settingValue != null)
                {
                    return settingValue.Value;
                }
            }

            //Not defined, get default value
            return settingDefinition.DefaultValue;
        }

        /// <inheritdoc/>
        public T GetSettingValue<T>(string name)
        {
            return (T)Convert.ChangeType(GetSettingValue(name), typeof(T));
        }

        /// <inheritdoc/>
        public IReadOnlyList<ISettingValue> GetAllSettingValues()
        {
            var settingDefinitions = new Dictionary<string, SettingDefinition>();
            var settingValues = new Dictionary<string, ISettingValue>();

            //Fill all setting with default values.
            foreach (var setting in _settingDefinitionManager.GetAllSettingDefinitions())
            {
                settingDefinitions[setting.Name] = setting;
                settingValues[setting.Name] = new SettingValueObject(setting.Name, setting.DefaultValue);
            }

            //Overwrite application settings
            foreach (var settingValue in GetAllSettingValuesForApplication())
            {
                var setting = settingDefinitions.GetOrDefault(settingValue.Name);
                if (setting != null && setting.Scopes.HasFlag(SettingScopes.Application))
                {
                    settingValues[settingValue.Name] = new SettingValueObject(settingValue.Name, settingValue.Value);
                }
            }

            //Overwrite tenant settings
            var tenantId = Session.TenantId;
            if (tenantId.HasValue)
            {
                foreach (var settingValue in GetAllSettingValuesForTenant(tenantId.Value))
                {
                    var setting = settingDefinitions.GetOrDefault(settingValue.Name);
                    if (setting != null && setting.Scopes.HasFlag(SettingScopes.Tenant))
                    {
                        settingValues[settingValue.Name] = new SettingValueObject(settingValue.Name, settingValue.Value);
                    }
                }
            }

            //Overwrite user settings
            var userId = Session.UserId;
            if (userId.HasValue)
            {
                foreach (var settingValue in GetAllSettingValuesForUser(userId.Value))
                {
                    var setting = settingDefinitions.GetOrDefault(settingValue.Name);
                    if (setting != null && setting.Scopes.HasFlag(SettingScopes.User))
                    {
                        settingValues[settingValue.Name] = new SettingValueObject(settingValue.Name, settingValue.Value);
                    }
                }
            }

            return settingValues.Values.ToImmutableList();
        }

        /// <inheritdoc/>
        public IReadOnlyList<ISettingValue> GetAllSettingValuesForApplication()
        {
            lock (_applicationSettings.Value)
            {
                return _applicationSettings.Value.Values
                    .Select(setting => new SettingValueObject(setting.Name, setting.Value))
                    .ToImmutableList();
            }
        }

        /// <inheritdoc/>
        public IReadOnlyList<ISettingValue> GetAllSettingValuesForTenant(Guid tenantId)
        {
            return GetReadOnlyTenantSettings(tenantId).Values
                .Select(setting => new SettingValueObject(setting.Name, setting.Value))
                .ToImmutableList();
        }

        /// <inheritdoc/>
        public IReadOnlyList<ISettingValue> GetAllSettingValuesForUser(Guid userId)
        {
            return GetReadOnlyUserSettings(userId).Values
                .Select(setting => new SettingValueObject(setting.Name, setting.Value))
                .ToImmutableList();
        }

        /// <inheritdoc/>
        [UnitOfWork]
        public virtual void ChangeSettingForApplication(string name, string value)
        {
            var settingValue = InsertOrUpdateOrDeleteSettingValue(name, value, null, null);
            lock (_applicationSettings.Value)
            {
                if (settingValue == null)
                {
                    _applicationSettings.Value.Remove(name);
                }
                else
                {
                    _applicationSettings.Value[name] = settingValue;
                }
            }
        }

        /// <inheritdoc/>
        [UnitOfWork]
        public virtual void ChangeSettingForTenant(Guid tenantId, string name, string value)
        {
            var settingValue = InsertOrUpdateOrDeleteSettingValue(name, value, tenantId, null);
            var cachedDictionary = GetTenantSettingsFromCache(tenantId);
            lock (cachedDictionary)
            {
                if (settingValue == null)
                {
                    cachedDictionary.Remove(name);
                }
                else
                {
                    cachedDictionary[name] = settingValue;
                }
            }
        }

        /// <inheritdoc/>
        [UnitOfWork]
        public virtual void ChangeSettingForUser(Guid userId, string name, string value)
        {
            var settingValue = InsertOrUpdateOrDeleteSettingValue(name, value, null, userId);
            var cachedDictionary = GetUserSettingsFromCache(userId);
            lock (cachedDictionary)
            {
                if (settingValue == null)
                {
                    cachedDictionary.Remove(name);
                }
                else
                {
                    cachedDictionary[name] = settingValue;
                }
            }
        }

        #endregion

        #region Private methods

        private SettingInfo InsertOrUpdateOrDeleteSettingValue(string name, string value, Guid? tenantId, Guid? userId)
        {
            if (tenantId.HasValue && userId.HasValue)
            {
                throw new ApplicationException("Both of tenantId and userId can not be set!");
            }

            var settingDefinition = _settingDefinitionManager.GetSettingDefinition(name);
            var settingValue = SettingStore.GetSettingOrNull(tenantId, userId, name);

            //Determine defaultValue
            var defaultValue = settingDefinition.DefaultValue;

            //For Tenant and User, Application's value overrides Setting Definition's default value.
            if (tenantId.HasValue || userId.HasValue)
            {
                var applicationValue = GetSettingValueForApplicationOrNull(name);
                if (applicationValue != null)
                {
                    defaultValue = applicationValue.Value;
                }
            }

            //For User, Tenants's value overrides Application's default value.
            if (userId.HasValue)
            {
                var currentTenantId = Session.TenantId;
                if (currentTenantId.HasValue)
                {
                    var tenantValue = GetSettingValueForTenantOrNull(currentTenantId.Value, name);
                    if (tenantValue != null)
                    {
                        defaultValue = tenantValue.Value;
                    }
                }
            }

            //No need to store on database if the value is the default value
            if (value == defaultValue)
            {
                if (settingValue != null)
                {
                    //_settingRepository.Delete(settingValue);
                    SettingStore.Delete(settingValue);
                }

                return null;
            }

            //It's not default value and not stored on database, so insert it
            if (settingValue == null)
            {
                settingValue = new SettingInfo
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Name = name,
                    Value = value
                };

                //_settingRepository.Insert(settingValue);
                SettingStore.Create(settingValue);
                return settingValue;
            }

            //It's same value as it's, no need to update
            if (settingValue.Value == value)
            {
                return settingValue;
            }

            //Update the setting on database.
            settingValue.Value = value;
            SettingStore.Update(settingValue);

            return settingValue;
        }

        private SettingInfo GetSettingValueForApplicationOrNull(string name)
        {
            lock (_applicationSettings.Value)
            {
                return _applicationSettings.Value.GetOrDefault(name);
            }
        }

        private SettingInfo GetSettingValueForTenantOrNull(Guid tenantId, string name)
        {
            return GetReadOnlyTenantSettings(tenantId).GetOrDefault(name);
        }

        private SettingInfo GetSettingValueForUserOrNull(Guid userId, string name)
        {
            return GetReadOnlyUserSettings(userId).GetOrDefault(name);
        }

        private Dictionary<string, SettingInfo> GetApplicationSettingsFromDatabase()
        {
            var dictionary = new Dictionary<string, SettingInfo>();

            var settingValues = SettingStore.GetAll(null, null);
            foreach (var settingValue in settingValues)
            {
                dictionary[settingValue.Name] = settingValue;
            }

            return dictionary;
        }


        private ImmutableDictionary<string, SettingInfo> GetReadOnlyTenantSettings(Guid tenantId)
        {
            var cachedDictionary = GetTenantSettingsFromCache(tenantId);
            lock (cachedDictionary)
            {
                return cachedDictionary.ToImmutableDictionary();
            }
        }
        private ImmutableDictionary<string, SettingInfo> GetReadOnlyUserSettings(Guid userId)
        {
            var cachedDictionary = GetUserSettingsFromCache(userId);
            lock (cachedDictionary)
            {
                return cachedDictionary.ToImmutableDictionary();
            }
        }

        private Dictionary<string, SettingInfo> GetTenantSettingsFromCache(Guid tenantId)
        {
            return _tenantSettingCache.Get(
                tenantId.ToString(),
                () =>
                {   //Getting from database
                    var dictionary = new Dictionary<string, SettingInfo>();

                    var settingValues = SettingStore.GetAll(tenantId, null);
                    foreach (var settingValue in settingValues)
                    {
                        dictionary[settingValue.Name] = settingValue;
                    }

                    return dictionary;
                });
        }

        private Dictionary<string, SettingInfo> GetUserSettingsFromCache(Guid userId)
        {
            return _userSettingCache.Get(
                userId.ToString(),
                () =>
                {   //Getting from database
                    var dictionary = new Dictionary<string, SettingInfo>();

                    var settingValues = SettingStore.GetAll(null, userId);
                    foreach (var settingValue in settingValues)
                    {
                        dictionary[settingValue.Name] = settingValue;
                    }

                    return dictionary;
                });
        }

        #endregion

        #region Nested classes

        private class SettingValueObject : ISettingValue
        {
            public string Name { get; private set; }

            public string Value { get; private set; }

            public SettingValueObject(string name, string value)
            {
                Value = value;
                Name = name;
            }
        }

        #endregion
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using Orchard.Caching;
using Orchard.Data;
using Orchard.Localization.Records;

namespace Orchard.Localization.Services {
    public class DefaultCultureManager : ICultureManager {
        private readonly IRepository<CultureRecord> _cultureRepository;
        private readonly ISignals _signals;
        private readonly IWorkContextAccessor _workContextAccessor;
        private readonly ICacheManager _cacheManager;

        public DefaultCultureManager(
            IRepository<CultureRecord> cultureRepository,
            ISignals signals,
            IWorkContextAccessor workContextAccessor,
            ICacheManager cacheManager) {

            _cultureRepository = cultureRepository;
            _signals = signals;
            _workContextAccessor = workContextAccessor;
            _cacheManager = cacheManager;
        }

        public IEnumerable<string> ListCultures() {
            return _cacheManager.Get("Cultures", true, context => {
                context.Monitor(_signals.When("culturesChanged"));

                return _cultureRepository.Table.Select(o => o.Culture).ToList();
            });
        }

        public void AddCulture(string cultureName) {
            if (!IsValidCulture(cultureName)) {
                throw new ArgumentException("cultureName");
            }

            if (ListCultures().Any(culture => culture == cultureName)) {
                return;
            }

            _cultureRepository.Create(new CultureRecord { Culture = cultureName });
            _signals.Trigger("culturesChanged");
        }

        public void DeleteCulture(string cultureName) {
            if (!IsValidCulture(cultureName)) {
                throw new ArgumentException("cultureName");
            }

            if (ListCultures().Any(culture => culture == cultureName)) {
                var culture = _cultureRepository.Get(cr => cr.Culture == cultureName);
                _cultureRepository.Delete(culture);
                _signals.Trigger("culturesChanged");
            }
        }

        public string GetCurrentCulture(HttpContextBase requestContext) {
            return _workContextAccessor.GetContext().CurrentCulture;
        }

        protected Dictionary<int, CultureRecord> GetAllCulturesById() {
            return _cacheManager.Get("all_culture_records_by_id", true, context => {
                context.Monitor(_signals.When("culturesChanged"));

                return _cultureRepository.Table
                    .ToDictionary(cr => cr.Id);
            });
        }
        public CultureRecord GetCultureById(int id) {
            var cultures = GetAllCulturesById();
            CultureRecord result;
            cultures.TryGetValue(id, out result);

            return result;
        }

        protected Dictionary<string, CultureRecord> GetAllCulturesByName() {
            return _cacheManager.Get("all_culture_records_by_name", true, context => {
                context.Monitor(_signals.When("culturesChanged"));

                return _cultureRepository.Table
                    .ToDictionary(cr => cr.Culture);
            });
        }
        public CultureRecord GetCultureByName(string cultureName) {
            if (string.IsNullOrWhiteSpace(cultureName)) {
                return null;
            }
            var cultures = GetAllCulturesByName();
            CultureRecord result;
            cultures.TryGetValue(cultureName, out result);

            return result;
        }

        public string GetSiteCulture() {
            return _workContextAccessor.GetContext().CurrentSite == null ? null : _workContextAccessor.GetContext().CurrentSite.SiteCulture;
        }

        // "<languagecode2>" or
        // "<languagecode2>-<country/regioncode2>" or
        // "<languagecode2>-<scripttag>-<country/regioncode2>"
        public bool IsValidCulture(string cultureName) {
            var segments = cultureName.Split('-');

            if (segments.Length == 0) {
                return false;
            }

            if (segments.Length > 3) {
                return false;
            }

            if (segments.Any(s => s.Length < 2)) {
                return false;
            }

            return true;
        }
    }
}

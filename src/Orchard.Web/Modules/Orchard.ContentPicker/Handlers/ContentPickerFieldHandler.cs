﻿using System.Linq;
using Orchard.ContentManagement;
using Orchard.ContentManagement.Handlers;
using Orchard.ContentManagement.MetaData;
using Orchard.ContentPicker.Fields;

namespace Orchard.ContentPicker.Handlers {
    public class ContentPickerFieldHandler : ContentHandler {
        private readonly IContentDefinitionManager _contentDefinitionManager;

        public ContentPickerFieldHandler(
            IContentDefinitionManager contentDefinitionManager) {
            
            _contentDefinitionManager = contentDefinitionManager;
        }

        protected override void Loading(LoadContentContext context) {
            base.Loading(context);

            var fields = context.ContentItem.Parts.SelectMany(x => x.Fields.Where(f => f.FieldDefinition.Name == typeof (ContentPickerField).Name)).Cast<ContentPickerField>();
            
            // define lazy initializer for ContentPickerField.ContentItems
            var contentTypeDefinition = _contentDefinitionManager.GetTypeDefinition(context.ContentType);
            if (contentTypeDefinition == null) {
                return;
            }

            foreach (var field in fields) {
                var localField = field;
                // Using context content item's ContentManager instead of injected one to avoid lifetime scope exceptions in case of LazyFields.
                field._contentItems.Loader(() => context.ContentItem.ContentManager.GetMany<ContentItem>(localField.Ids, VersionOptions.Published, QueryHints.Empty));
            }
        }
    }
}
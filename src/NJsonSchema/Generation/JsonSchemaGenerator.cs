﻿//-----------------------------------------------------------------------
// <copyright file="JsonSchemaGenerator.cs" company="NJsonSchema">
//     Copyright (c) Rico Suter. All rights reserved.
// </copyright>
// <license>https://github.com/rsuter/NJsonSchema/blob/master/LICENSE.md</license>
// <author>Rico Suter, mail@rsuter.com</author>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NJsonSchema.Annotations;
using NJsonSchema.Converters;
using NJsonSchema.Infrastructure;
using System.Runtime.Serialization;
using NJsonSchema.Generation.TypeMappers;

namespace NJsonSchema.Generation
{
    /// <summary>Generates a <see cref="JsonSchema4"/> object for a given type. </summary>
    public class JsonSchemaGenerator
    {
        private static readonly Dictionary<string, string> DataTypeFormats = new Dictionary<string, string>
        {
            {"DateTime", JsonFormatStrings.DateTime},
            {"Date", JsonFormatStrings.Date},
            {"Time", JsonFormatStrings.Time},
            {"EmailAddress", JsonFormatStrings.Email},
            {"PhoneNumber", JsonFormatStrings.Phone},
            {"Url", JsonFormatStrings.Uri}
        };

        /// <summary>Initializes a new instance of the <see cref="JsonSchemaGenerator"/> class.</summary>
        /// <param name="settings">The settings.</param>
        public JsonSchemaGenerator(JsonSchemaGeneratorSettings settings)
        {
            Settings = settings;
        }

        /// <summary>Gets the settings.</summary>
        public JsonSchemaGeneratorSettings Settings { get; }

        /// <summary>Generates a <see cref="JsonSchema4" /> object for the given type and adds the mapping to the given resolver.</summary>
        /// <param name="type">The type.</param>
        /// <returns>The schema.</returns>
        /// <exception cref="InvalidOperationException">Could not find value type of dictionary type.</exception>
        public async Task<JsonSchema4> GenerateAsync(Type type)
        {
            var schema = new JsonSchema4();
            var schemaResolver = new JsonSchemaResolver(schema, Settings);
            await GenerateAsync(type, null, schema, schemaResolver).ConfigureAwait(false);
            return schema;
        }

        /// <summary>Generates a <see cref="JsonSchema4" /> object for the given type and adds the mapping to the given resolver.</summary>
        /// <param name="type">The type.</param>
        /// <param name="schemaResolver">The schema resolver.</param>
        /// <returns>The schema.</returns>
        /// <exception cref="InvalidOperationException">Could not find value type of dictionary type.</exception>
        public Task<JsonSchema4> GenerateAsync(Type type, JsonSchemaResolver schemaResolver)
        {
            return GenerateAsync<JsonSchema4>(type, schemaResolver);
        }

        /// <summary>Generates a <see cref="JsonSchema4" /> object for the given type and adds the mapping to the given resolver.</summary>
        /// <param name="type">The type.</param>
        /// <param name="schemaResolver">The schema resolver.</param>
        /// <returns>The schema.</returns>
        /// <exception cref="InvalidOperationException">Could not find value type of dictionary type.</exception>
        public Task<TSchemaType> GenerateAsync<TSchemaType>(Type type, JsonSchemaResolver schemaResolver)
            where TSchemaType : JsonSchema4, new()
        {
            return GenerateAsync<TSchemaType>(type, null, schemaResolver);
        }

        /// <summary>Generates a <see cref="JsonSchema4" /> object for the given type and adds the mapping to the given resolver.</summary>
        /// <param name="type">The type.</param>
        /// <param name="parentAttributes">The parent property or parameter attributes.</param>
        /// <param name="schemaResolver">The schema resolver.</param>
        /// <returns>The schema.</returns>
        /// <exception cref="InvalidOperationException">Could not find value type of dictionary type.</exception>
        public async Task<JsonSchema4> GenerateAsync(Type type, IEnumerable<Attribute> parentAttributes, JsonSchemaResolver schemaResolver)
        {
            return await GenerateAsync<JsonSchema4>(type, parentAttributes, schemaResolver).ConfigureAwait(false);
        }

        /// <summary>Generates a <see cref="JsonSchema4" /> object for the given type and adds the mapping to the given resolver.</summary>
        /// <param name="type">The type.</param>
        /// <param name="parentAttributes">The parent property or parameter attributes.</param>
        /// <param name="schemaResolver">The schema resolver.</param>
        /// <returns>The schema.</returns>
        /// <exception cref="InvalidOperationException">Could not find value type of dictionary type.</exception>
        public async Task<TSchemaType> GenerateAsync<TSchemaType>(Type type, IEnumerable<Attribute> parentAttributes, JsonSchemaResolver schemaResolver)
            where TSchemaType : JsonSchema4, new()
        {
            var schema = new TSchemaType();
            await GenerateAsync(type, parentAttributes, schema, schemaResolver).ConfigureAwait(false);
            return schema;
        }

        /// <summary>Generates a <see cref="JsonSchema4" /> object for the given type and adds the mapping to the given resolver.</summary>
        /// <typeparam name="TSchemaType">The type of the schema.</typeparam>
        /// <param name="type">The type.</param>
        /// <param name="parentAttributes">The parent property or parameter attributes.</param>
        /// <param name="schema">The schema.</param>
        /// <param name="schemaResolver">The schema resolver.</param>
        /// <returns>The schema.</returns>
        /// <exception cref="InvalidOperationException">Could not find value type of dictionary type.</exception>
        public virtual async Task GenerateAsync<TSchemaType>(Type type, IEnumerable<Attribute> parentAttributes, TSchemaType schema, JsonSchemaResolver schemaResolver)
            where TSchemaType : JsonSchema4, new()
        {
            if (await TryHandleSpecialTypesAsync(type, schema, schemaResolver, parentAttributes))
            {
                await ApplySchemaProcessorsAsync(type, schema, schemaResolver);
                return;
            }

            if (schemaResolver.RootObject == schema)
                schema.Title = Settings.SchemaNameGenerator.Generate(type);

            ApplyExtensionDataAttributes(type, schema, parentAttributes);

            var contract = ResolveContract(type);
            var typeDescription = JsonObjectTypeDescription.FromType(type, contract, parentAttributes, Settings.DefaultEnumHandling);
            if (typeDescription.Type.HasFlag(JsonObjectType.Object))
            {
                if (typeDescription.IsDictionary)
                {
                    typeDescription.ApplyType(schema);
                    await GenerateDictionaryAsync(type, schema, schemaResolver).ConfigureAwait(false);
                }
                else
                {
                    if (schemaResolver.HasSchema(type, false))
                        schema.SchemaReference = schemaResolver.GetSchema(type, false);
                    else if (schema.GetType() == typeof(JsonSchema4))
                    {
                        typeDescription.ApplyType(schema);
                        schema.Description = await type.GetTypeInfo().GetDescriptionAsync(type.GetTypeInfo().GetCustomAttributes()).ConfigureAwait(false);
                        await GenerateObjectAsync(type, contract, schema, schemaResolver).ConfigureAwait(false);
                    }
                    else
                        schema.SchemaReference = await GenerateAsync(type, parentAttributes, schemaResolver).ConfigureAwait(false);
                }
            }
            else if (type.GetTypeInfo().IsEnum)
            {
                var isIntegerEnumeration = typeDescription.Type == JsonObjectType.Integer;
                if (schemaResolver.HasSchema(type, isIntegerEnumeration))
                    schema.SchemaReference = schemaResolver.GetSchema(type, isIntegerEnumeration);
                else if (schema.GetType() == typeof(JsonSchema4))
                {
                    LoadEnumerations(type, schema, typeDescription);

                    typeDescription.ApplyType(schema);
                    schema.Description = await type.GetXmlSummaryAsync().ConfigureAwait(false);

                    schemaResolver.AddSchema(type, isIntegerEnumeration, schema);
                }
                else
                    schema.SchemaReference = await GenerateAsync(type, parentAttributes, schemaResolver).ConfigureAwait(false);
            }
            else if (typeDescription.Type.HasFlag(JsonObjectType.Array))
            {
                typeDescription.ApplyType(schema);

                var itemType = type.GetEnumerableItemType();
                if (itemType != null)
                    schema.Item = await GenerateWithReferenceAsync(schemaResolver, itemType).ConfigureAwait(false);
                else
                    schema.Item = JsonSchema4.CreateAnySchema();
            }
            else
                typeDescription.ApplyType(schema);

            await ApplySchemaProcessorsAsync(type, schema, schemaResolver);
        }

        private async Task ApplySchemaProcessorsAsync(Type type, JsonSchema4 schema, JsonSchemaResolver schemaResolver)
        {
            var context = new SchemaProcessorContext(type, schema, schemaResolver, this);
            foreach (var processor in Settings.SchemaProcessors)
                await processor.ProcessAsync(context);
        }

        private async Task<JsonSchema4> GenerateWithReferenceAsync(JsonSchemaResolver schemaResolver, Type itemType)
        {
            var schema = await GenerateAsync(itemType, schemaResolver).ConfigureAwait(false);

            if (Settings.GenerateXmlObjects)
                schema.GenerateXmlObjectForItemType(itemType);

            if (RequiresSchemaReference(itemType, null))
                return new JsonSchema4 { SchemaReference = schema };

            return schema;
        }

        private void ApplyExtensionDataAttributes<TSchemaType>(Type type, TSchemaType schema, IEnumerable<Attribute> parentAttributes)
            where TSchemaType : JsonSchema4, new()
        {
            if (parentAttributes == null)
            {
                // class
                var extensionDataAttributes = type.GetTypeInfo().GetCustomAttributes<JsonSchemaExtensionDataAttribute>().ToArray();
                if (extensionDataAttributes.Any())
                    schema.ExtensionData = extensionDataAttributes.ToDictionary(a => a.Property, a => a.Value);
            }
            else
            {
                // property or parameter
                var extensionDataAttributes = parentAttributes.OfType<JsonSchemaExtensionDataAttribute>().ToArray();
                if (extensionDataAttributes.Any())
                    schema.ExtensionData = extensionDataAttributes.ToDictionary(a => a.Property, a => a.Value);
            }
        }

        private async Task<bool> TryHandleSpecialTypesAsync<TSchemaType>(Type type, TSchemaType schema,
            JsonSchemaResolver schemaResolver, IEnumerable<Attribute> parentAttributes)
            where TSchemaType : JsonSchema4, new()
        {
            var typeMapper = Settings.TypeMappers.FirstOrDefault(m => m.MappedType == type);
            if (typeMapper == null && type.GetTypeInfo().IsGenericType)
            {
                var genericType = type.GetGenericTypeDefinition();
                typeMapper = Settings.TypeMappers.FirstOrDefault(m => m.MappedType == genericType);
            }

            if (typeMapper != null)
            {
                await typeMapper.GenerateSchemaAsync(schema, new TypeMapperContext(type, this, schemaResolver, parentAttributes));
                return true;
            }

            if (type == typeof(JObject) || type == typeof(JToken) || type == typeof(object))
                return true;

            return false;
        }

        /// <exception cref="InvalidOperationException">Could not find value type of dictionary type.</exception>
        private async Task GenerateDictionaryAsync<TSchemaType>(Type type, TSchemaType schema, JsonSchemaResolver schemaResolver)
            where TSchemaType : JsonSchema4, new()
        {
            var genericTypeArguments = type.GetGenericTypeArguments();

            var valueType = genericTypeArguments.Length == 2 ? genericTypeArguments[1] : typeof(object);
            if (valueType == typeof(object))
                schema.AdditionalPropertiesSchema = JsonSchema4.CreateAnySchema();
            else
            {
                var additionalPropertiesSchema = await GenerateAsync(valueType, schemaResolver).ConfigureAwait(false);
                if (RequiresSchemaReference(valueType, null))
                {
                    schema.AdditionalPropertiesSchema = new JsonSchema4
                    {
                        SchemaReference = additionalPropertiesSchema
                    };
                }
                else
                    schema.AdditionalPropertiesSchema = additionalPropertiesSchema;
            }

            schema.AllowAdditionalProperties = true;
        }

        /// <summary>Generates the properties for the given type and schema.</summary>
        /// <typeparam name="TSchemaType">The type of the schema type.</typeparam>
        /// <param name="type">The types.</param>
        /// <param name="contract">The JSON object contract.</param>
        /// <param name="schema">The properties</param>
        /// <param name="schemaResolver">The schema resolver.</param>
        /// <returns></returns>
        protected virtual async Task GenerateObjectAsync<TSchemaType>(Type type, JsonContract contract, TSchemaType schema, JsonSchemaResolver schemaResolver)
            where TSchemaType : JsonSchema4, new()
        {
            schemaResolver.AddSchema(type, false, schema);

            schema.AllowAdditionalProperties = false;

            await GeneratePropertiesAndInheritanceAsync(type, contract, schema, schemaResolver).ConfigureAwait(false);

            if (Settings.GenerateKnownTypes)
                await GenerateKnownTypesAsync(type, schemaResolver).ConfigureAwait(false);

            if (Settings.GenerateXmlObjects)
                schema.GenerateXmlObjectForType(type);
        }

        private async Task GeneratePropertiesAndInheritanceAsync(Type type, JsonContract contract, JsonSchema4 schema, JsonSchemaResolver schemaResolver)
        {
#if !LEGACY
            var propertiesAndFields = type.GetTypeInfo()
                .DeclaredFields
                .Where(f => f.IsPublic)
                .OfType<MemberInfo>()
                .Concat(
                    type.GetTypeInfo().DeclaredProperties
                    .Where(p => p.GetMethod?.IsPublic == true || p.SetMethod?.IsPublic == true)
                )
                .ToList();
#else
            var propertiesAndFields = type.GetTypeInfo()
                .GetFields(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)
                .OfType<MemberInfo>()
                .Concat(
                    type.GetTypeInfo()
                    .GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetGetMethod()?.IsPublic == true || p.GetSetMethod()?.IsPublic == true)
                )
                .ToList();
#endif

            var objectContract = contract as JsonObjectContract;
            if (objectContract != null)
            {
                foreach (var property in objectContract.Properties.Where(p => p.DeclaringType == type))
                {
                    bool shouldSerialize;
                    try
                    {
                        shouldSerialize = property.ShouldSerialize?.Invoke(null) != false;
                    }
                    catch
                    {
                        shouldSerialize = true;
                    }

                    if (shouldSerialize)
                    {
                        var info = propertiesAndFields.FirstOrDefault(p => p.Name == property.UnderlyingName);
                        var propertyInfo = info as PropertyInfo;
#if !LEGACY
                        if (Settings.GenerateAbstractProperties || propertyInfo == null || 
                            (propertyInfo.GetMethod?.IsAbstract != true && propertyInfo.SetMethod?.IsAbstract != true))
#else
                        if (Settings.GenerateAbstractProperties || propertyInfo == null ||
                            (propertyInfo.GetGetMethod()?.IsAbstract != true && propertyInfo.GetSetMethod()?.IsAbstract != true))
#endif
                        {
                            await LoadPropertyOrFieldAsync(property, info, type, schema, schemaResolver).ConfigureAwait(false);
                        }
                    }
                }
            }
            else
            {
                // TODO: Remove this hacky code (used to support serialization of exceptions and restore the old behavior [pre 9.x])

                var allowedProperties = GetTypeProperties(type);
                foreach (var info in propertiesAndFields.Where(m => allowedProperties == null || allowedProperties.Contains(m.Name)))
                {
                    var attribute = info.GetCustomAttributes(true).OfType<JsonPropertyAttribute>().SingleOrDefault();
                    var propertyType = (info as PropertyInfo)?.PropertyType ?? ((FieldInfo)info).FieldType;
                    var property = new Newtonsoft.Json.Serialization.JsonProperty
                    {
                        AttributeProvider = new ReflectionAttributeProvider(info),
                        PropertyType = propertyType,
                        Ignored = IsPropertyIgnored(propertyType, type, info.GetCustomAttributes(true).OfType<Attribute>().ToArray())
                    };

                    if (attribute != null)
                    {
                        property.PropertyName = attribute.PropertyName ?? info.Name;
                        property.Required = attribute.Required;
                        property.DefaultValueHandling = attribute.DefaultValueHandling;
                        property.TypeNameHandling = attribute.TypeNameHandling;
                        property.NullValueHandling = attribute.NullValueHandling;
                        property.TypeNameHandling = attribute.TypeNameHandling;
                    }
                    else
                    {
                        property.PropertyName = info.Name;
                    }

                    await LoadPropertyOrFieldAsync(property, info, type, schema, schemaResolver).ConfigureAwait(false);
                }
            }

            await GenerateInheritanceAsync(type, schema, schemaResolver).ConfigureAwait(false);
        }

        /// <summary>Gets the properties of the given type or null to take all properties.</summary>
        /// <param name="type">The type.</param>
        /// <returns>The property names or null for all.</returns>
        protected virtual string[] GetTypeProperties(Type type)
        {
            if (type == typeof(Exception))
                return new[] { "InnerException", "Message", "Source", "StackTrace" };

            return null;
        }

        private async Task GenerateKnownTypesAsync(Type objectType, JsonSchemaResolver schemaResolver)
        {
            var type = objectType;
            do
            {
                var knownTypeAttributes = type.GetTypeInfo().GetCustomAttributes(false).Where(a => a.GetType().Name == "KnownTypeAttribute").OfType<Attribute>();
                foreach (dynamic attribute in knownTypeAttributes)
                {
                    if (attribute.Type != null)
                        await AddKnownTypeAsync(attribute.Type, schemaResolver);
                    else if (attribute.MethodName != null)
                    {
                        var methodInfo = type.GetRuntimeMethod((string)attribute.MethodName, new Type[0]);
                        if (methodInfo != null)
                        {
                            var knownTypes = methodInfo.Invoke(null, null) as Type[];
                            if (knownTypes != null)
                            {
                                foreach (var knownType in knownTypes)
                                    await AddKnownTypeAsync(knownType, schemaResolver);
                            }
                        }
                    }
                    else
                        throw new ArgumentException($"A KnownType attribute on {type.FullName} does not specify a type or a method name.", nameof(type));
                }
                type = type.GetTypeInfo().BaseType;
            } while (type != null);
        }

        private async Task AddKnownTypeAsync(Type type, JsonSchemaResolver schemaResolver)
        {
            var typeDescription = JsonObjectTypeDescription.FromType(type, ResolveContract(type), null, Settings.DefaultEnumHandling);
            var isIntegerEnum = typeDescription.Type == JsonObjectType.Integer;

            if (!schemaResolver.HasSchema(type, isIntegerEnum))
                await GenerateAsync(type, schemaResolver).ConfigureAwait(false);
        }

        private async Task GenerateInheritanceAsync(Type type, JsonSchema4 schema, JsonSchemaResolver schemaResolver)
        {
            GenerateInheritanceDiscriminator(type, schema);

            var baseType = type.GetTypeInfo().BaseType;
            if (baseType != null && baseType != typeof(object))
            {
                if (Settings.FlattenInheritanceHierarchy)
                {
                    await GeneratePropertiesAndInheritanceAsync(baseType, (JsonObjectContract)ResolveContract(baseType), schema, schemaResolver).ConfigureAwait(false);
                }
                else
                {
                    var baseSchema = await GenerateAsync(baseType, schemaResolver).ConfigureAwait(false);
                    if (RequiresSchemaReference(baseType, null))
                    {
                        if (schemaResolver.RootObject != baseSchema.ActualSchema)
                            schemaResolver.AppendSchema(baseSchema.ActualSchema, Settings.SchemaNameGenerator.Generate(baseType));

                        schema.AllOf.Add(new JsonSchema4
                        {
                            SchemaReference = baseSchema.ActualSchema
                        });
                    }
                    else
                        schema.AllOf.Add(baseSchema);
                }
            }
        }

        private void GenerateInheritanceDiscriminator(Type type, JsonSchema4 schema)
        {
            if (!Settings.FlattenInheritanceHierarchy)
            {
                var discriminator = TryGetInheritanceDiscriminator(type.GetTypeInfo().GetCustomAttributes(false).OfType<Attribute>());
                if (!string.IsNullOrEmpty(discriminator))
                {
                    if (schema.Properties.ContainsKey(discriminator))
                        throw new InvalidOperationException("The JSON property '" + discriminator + "' is defined multiple times on type '" + type.FullName + "'.");

                    schema.Discriminator = discriminator;
                    schema.Properties[discriminator] = new JsonProperty
                    {
                        Type = JsonObjectType.String,
                        IsRequired = true
                    };
                }
            }
        }

        private string TryGetInheritanceDiscriminator(IEnumerable<Attribute> typeAttributes)
        {
            dynamic jsonConverterAttribute = typeAttributes?.FirstOrDefault(a => a.GetType().Name == "JsonConverterAttribute");
            if (jsonConverterAttribute != null)
            {
                var converterType = (Type)jsonConverterAttribute.ConverterType;
                if (converterType.Name == "JsonInheritanceConverter")
                {
                    if (jsonConverterAttribute.ConverterParameters != null && jsonConverterAttribute.ConverterParameters.Length > 0)
                        return jsonConverterAttribute.ConverterParameters[0];
                    return JsonInheritanceConverter.DefaultDiscriminatorName;
                }
            }
            return null;
        }

        private void LoadEnumerations(Type type, JsonSchema4 schema, JsonObjectTypeDescription typeDescription)
        {
            schema.Type = typeDescription.Type;
            schema.Enumeration.Clear();
            schema.EnumerationNames.Clear();

            foreach (var enumName in Enum.GetNames(type))
            {
                if (typeDescription.Type == JsonObjectType.Integer)
                {
                    var value = Convert.ChangeType(Enum.Parse(type, enumName), Enum.GetUnderlyingType(type));
                    schema.Enumeration.Add(value);
                }
                else
                {
                    var attributes = type.GetTypeInfo().GetDeclaredField(enumName).GetCustomAttributes(); // EnumMember only checked if StringEnumConverter is used
                    dynamic enumMemberAttribute = attributes.TryGetIfAssignableTo("System.Runtime.Serialization.EnumMemberAttribute");
                    if (enumMemberAttribute != null && !string.IsNullOrEmpty(enumMemberAttribute.Value))
                        schema.Enumeration.Add((string)enumMemberAttribute.Value);
                    else
                        schema.Enumeration.Add(enumName);
                }

                schema.EnumerationNames.Add(enumName);
            }
        }

        private async Task LoadPropertyOrFieldAsync(Newtonsoft.Json.Serialization.JsonProperty property, MemberInfo propertyInfo, Type parentType, JsonSchema4 parentSchema, JsonSchemaResolver schemaResolver)
        {
            var propertyType = property.PropertyType;
            var propertyAttributes = property.AttributeProvider.GetAttributes(true).ToArray();
            var propertyTypeDescription = JsonObjectTypeDescription.FromType(propertyType, ResolveContract(propertyType), null, Settings.DefaultEnumHandling);
            if (property.Ignored == false && IsPropertyIgnoredBySettings(propertyType, parentType, propertyAttributes) == false)
            {
                JsonProperty jsonProperty;

                if (propertyType.Name == "Nullable`1")
#if !LEGACY
                    propertyType = propertyType.GenericTypeArguments[0];
#else
                    propertyType = propertyType.GetGenericArguments()[0];
#endif

                var requiresSchemaReference = RequiresSchemaReference(propertyType, propertyAttributes);
                if (requiresSchemaReference)
                {
                    var propertySchema = await GenerateAsync(propertyType, propertyAttributes, schemaResolver).ConfigureAwait(false);

                    // The schema is automatically added to Definitions if it is missing in JsonPathUtilities.GetJsonPath()
                    if (Settings.NullHandling == NullHandling.JsonSchema)
                    {
                        jsonProperty = new JsonProperty();
                        jsonProperty.OneOf.Add(new JsonSchema4
                        {
                            SchemaReference = propertySchema.ActualSchema
                        });
                    }
                    else
                    {
                        jsonProperty = new JsonProperty
                        {
                            SchemaReference = propertySchema.ActualSchema
                        };
                    }
                }
                else
                    jsonProperty = await GenerateAsync<JsonProperty>(propertyType, propertyAttributes, schemaResolver).ConfigureAwait(false);

                var contractResolver = Settings.ActualContractResolver as DefaultContractResolver;
                var propertyName = contractResolver != null ?
                    contractResolver.GetResolvedPropertyName(property.PropertyName) :
                    property.PropertyName;

                if (parentSchema.Properties.ContainsKey(propertyName))
                    throw new InvalidOperationException("The JSON property '" + propertyName + "' is defined multiple times on type '" + parentType.FullName + "'.");

                if (Settings.GenerateXmlObjects)
                    jsonProperty.GenerateXmlObjectForProperty(parentType, propertyName, propertyAttributes);

                parentSchema.Properties.Add(propertyName, jsonProperty);

                var requiredAttribute = propertyAttributes.TryGetIfAssignableTo("System.ComponentModel.DataAnnotations.RequiredAttribute");

                var hasJsonNetAttributeRequired = property.Required == Required.Always || property.Required == Required.AllowNull;
                var isDataContractMemberRequired = GetDataMemberAttribute(parentType, propertyAttributes)?.IsRequired == true;

                var hasRequiredAttribute = requiredAttribute != null;
                if (hasRequiredAttribute || isDataContractMemberRequired || hasJsonNetAttributeRequired)
                    parentSchema.RequiredProperties.Add(propertyName);

                var isNullable = propertyTypeDescription.IsNullable &&
                    hasRequiredAttribute == false &&
                    isDataContractMemberRequired == false &&
                    (property.Required == Required.Default || property.Required == Required.AllowNull);

                if (isNullable)
                {
                    if (Settings.NullHandling == NullHandling.JsonSchema)
                    {
                        if (requiresSchemaReference)
                            jsonProperty.OneOf.Add(new JsonSchema4 { Type = JsonObjectType.Null });
                        else if (jsonProperty.Type == JsonObjectType.None)
                        {
                            jsonProperty.OneOf.Add(new JsonSchema4 { Type = JsonObjectType.None });
                            jsonProperty.OneOf.Add(new JsonSchema4 { Type = JsonObjectType.Null });
                        }
                        else
                            jsonProperty.Type = jsonProperty.Type | JsonObjectType.Null;
                    }
                }
                else if (Settings.NullHandling == NullHandling.Swagger)
                {
                    if (!parentSchema.RequiredProperties.Contains(propertyName))
                        parentSchema.RequiredProperties.Add(propertyName);
                }

                dynamic readOnlyAttribute = propertyAttributes.TryGetIfAssignableTo("System.ComponentModel.ReadOnlyAttribute");
                if (readOnlyAttribute != null)
                    jsonProperty.IsReadOnly = readOnlyAttribute.IsReadOnly;

                jsonProperty.Description = await propertyInfo.GetDescriptionAsync(propertyAttributes).ConfigureAwait(false);

                ApplyPropertyAnnotations(jsonProperty, property, parentType, propertyAttributes, propertyTypeDescription);
            }
        }

        private bool RequiresSchemaReference(Type type, IEnumerable<Attribute> parentAttributes)
        {
            var typeDescription = JsonObjectTypeDescription.FromType(type, ResolveContract(type), parentAttributes, Settings.DefaultEnumHandling);

            var typeMapper = Settings.TypeMappers.FirstOrDefault(m => m.MappedType == type);
            if (typeMapper != null)
                return typeMapper.UseReference;

            return !typeDescription.IsDictionary && (typeDescription.Type.HasFlag(JsonObjectType.Object) || typeDescription.IsEnum);
        }

        private JsonContract ResolveContract(Type type) => Settings.ActualContractResolver.ResolveContract(type);

        private bool IsPropertyIgnored(Type propertyType, Type parentType, Attribute[] propertyAttributes)
        {
            if (propertyAttributes.Any(a => a is JsonIgnoreAttribute))
                return true;

            if (HasDataContractAttribute(parentType) && GetDataMemberAttribute(parentType, propertyAttributes) == null && !propertyAttributes.Any(a => a is JsonPropertyAttribute))
                return true;

            return IsPropertyIgnoredBySettings(propertyType, parentType, propertyAttributes);
        }

        private bool IsPropertyIgnoredBySettings(Type propertyType, Type parentType, Attribute[] propertyAttributes)
        {
            if (Settings.IgnoreObsoleteProperties && propertyAttributes.Any(a => a is ObsoleteAttribute))
                return true;

            return false;
        }

        private static dynamic GetDataMemberAttribute(Type parentType, Attribute[] propertyAttributes)
        {
            if (!HasDataContractAttribute(parentType))
                return null;

            return propertyAttributes.FirstOrDefault(a => a.GetType().Name == "DataMemberAttribute");
        }

        private static bool HasDataContractAttribute(Type parentType)
        {
            return parentType.GetTypeInfo().GetCustomAttributes().Any(a => a.GetType().Name == "DataContractAttribute");
        }

        /// <summary>Applies the property annotations to the JSON property.</summary>
        /// <param name="jsonProperty">The JSON property.</param>
        /// <param name="property"></param>
        /// <param name="parentType">The type of the parent.</param>
        /// <param name="attributes">The attributes.</param>
        /// <param name="propertyTypeDescription">The property type description.</param>
        public void ApplyPropertyAnnotations(JsonSchema4 jsonProperty, Newtonsoft.Json.Serialization.JsonProperty property, Type parentType, IEnumerable<Attribute> attributes, JsonObjectTypeDescription propertyTypeDescription)
        {
            // TODO: Refactor out

            dynamic displayAttribute = attributes.TryGetIfAssignableTo("System.ComponentModel.DataAnnotations.DisplayAttribute");
            if (displayAttribute != null && displayAttribute.Name != null)
                jsonProperty.Title = displayAttribute.Name;

            if (property != null)
                jsonProperty.Default = ConvertDefaultValue(property);
            else
            {
                dynamic defaultValueAttribute = attributes.TryGetIfAssignableTo("System.ComponentModel.DefaultValueAttribute");
                if (defaultValueAttribute != null)
                    jsonProperty.Default = defaultValueAttribute.Value;
            }

            dynamic regexAttribute = attributes.TryGetIfAssignableTo("System.ComponentModel.DataAnnotations.RegularExpressionAttribute");
            if (regexAttribute != null)
                jsonProperty.Pattern = regexAttribute.Pattern;

            if (propertyTypeDescription.Type == JsonObjectType.Number ||
                propertyTypeDescription.Type == JsonObjectType.Integer)
            {
                dynamic rangeAttribute = attributes.TryGetIfAssignableTo("System.ComponentModel.DataAnnotations.RangeAttribute");
                if (rangeAttribute != null)
                {
                    if (rangeAttribute.Minimum != null && rangeAttribute.Minimum > double.MinValue)
                        jsonProperty.Minimum = (decimal?)(double)rangeAttribute.Minimum;
                    if (rangeAttribute.Maximum != null && rangeAttribute.Maximum < double.MaxValue)
                        jsonProperty.Maximum = (decimal?)(double)rangeAttribute.Maximum;
                }

                var multipleOfAttribute = attributes.OfType<MultipleOfAttribute>().SingleOrDefault();
                if (multipleOfAttribute != null)
                    jsonProperty.MultipleOf = multipleOfAttribute.MultipleOf;
            }

            dynamic minLengthAttribute = attributes.TryGetIfAssignableTo("System.ComponentModel.DataAnnotations.MinLengthAttribute");
            if (minLengthAttribute != null && minLengthAttribute.Length != null)
            {
                if (propertyTypeDescription.Type == JsonObjectType.String)
                    jsonProperty.MinLength = minLengthAttribute.Length;
                else if (propertyTypeDescription.Type == JsonObjectType.Array)
                    jsonProperty.MinItems = minLengthAttribute.Length;
            }

            dynamic maxLengthAttribute = attributes.TryGetIfAssignableTo("System.ComponentModel.DataAnnotations.MaxLengthAttribute");
            if (maxLengthAttribute != null && maxLengthAttribute.Length != null)
            {
                if (propertyTypeDescription.Type == JsonObjectType.String)
                    jsonProperty.MaxLength = maxLengthAttribute.Length;
                else if (propertyTypeDescription.Type == JsonObjectType.Array)
                    jsonProperty.MaxItems = maxLengthAttribute.Length;
            }

            dynamic stringLengthAttribute = attributes.TryGetIfAssignableTo("System.ComponentModel.DataAnnotations.StringLengthAttribute");
            if (stringLengthAttribute != null)
            {
                if (propertyTypeDescription.Type == JsonObjectType.String)
                {
                    jsonProperty.MinLength = stringLengthAttribute.MinimumLength;
                    jsonProperty.MaxLength = stringLengthAttribute.MaximumLength;
                }
            }

            dynamic dataTypeAttribute = attributes.TryGetIfAssignableTo("System.ComponentModel.DataAnnotations.DataTypeAttribute");
            if (dataTypeAttribute != null)
            {
                var dataType = dataTypeAttribute.DataType.ToString();
                if (DataTypeFormats.ContainsKey(dataType))
                    jsonProperty.Format = DataTypeFormats[dataType];
            }
        }

        private object ConvertDefaultValue(Newtonsoft.Json.Serialization.JsonProperty property)
        {
            if (property.DefaultValue != null && property.DefaultValue.GetType().GetTypeInfo().IsEnum)
            {
                var hasStringEnumConverter = typeof(StringEnumConverter).GetTypeInfo().IsAssignableFrom(property.Converter?.GetType().GetTypeInfo());
                if (hasStringEnumConverter)
                    return property.DefaultValue.ToString();
                else
                    return (int)property.DefaultValue;
            }
            else
                return property.DefaultValue;
        }
    }
}

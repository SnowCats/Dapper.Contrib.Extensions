using System.Data;
using System.Reflection;
using System.Text;
using System.Collections.Concurrent;
using System.Reflection.Emit;

using Dapper;
using Dawning.Auth.Dapper.Contrib;
using System.Linq.Expressions;
using System.Collections;

namespace Dawning.Auth.Dapper.Contrib
{
    /// <summary>
    /// The Dapper.Contrib extensions for Dapper
    /// </summary>
    public static partial class SqlMapperExtensions
    {
        /// <summary>
        /// Defined a proxy object with a possibly dirty state.
        /// </summary>
        public interface IProxy //must be kept public
        {
            /// <summary>
            /// Whether the object has been changed.
            /// </summary>
            bool IsDirty { get; set; }
        }

        /// <summary>
        /// Defines a table name mapper for getting table names from types.
        /// </summary>
        public interface ITableNameMapper
        {
            /// <summary>
            /// Gets a table name from a given <see cref="Type"/>.
            /// </summary>
            /// <param name="type">The <see cref="Type"/> to get a name from.</param>
            /// <returns>The table name for the given <paramref name="type"/>.</returns>
            string GetTableName(Type type);
        }

        /// <summary>
        /// The function to get a database type from the given <see cref="IDbConnection"/>.
        /// </summary>
        /// <param name="connection">The connection to get a database type name from.</param>
        public delegate string GetDatabaseTypeDelegate(IDbConnection connection);
        /// <summary>
        /// The function to get a table name from a given <see cref="Type"/>
        /// </summary>
        /// <param name="type">The <see cref="Type"/> to get a table name for.</param>
        public delegate string TableNameMapperDelegate(Type type);

        private static readonly ConcurrentDictionary<RuntimeTypeHandle, IEnumerable<PropertyInfo>> KeyProperties = new ConcurrentDictionary<RuntimeTypeHandle, IEnumerable<PropertyInfo>>();
        private static readonly ConcurrentDictionary<RuntimeTypeHandle, IEnumerable<PropertyInfo>> ExplicitKeyProperties = new ConcurrentDictionary<RuntimeTypeHandle, IEnumerable<PropertyInfo>>();
        private static readonly ConcurrentDictionary<RuntimeTypeHandle, IEnumerable<PropertyInfo>> TypeProperties = new ConcurrentDictionary<RuntimeTypeHandle, IEnumerable<PropertyInfo>>();
        private static readonly ConcurrentDictionary<RuntimeTypeHandle, IEnumerable<PropertyInfo>> ComputedProperties = new ConcurrentDictionary<RuntimeTypeHandle, IEnumerable<PropertyInfo>>();
        private static readonly ConcurrentDictionary<RuntimeTypeHandle, IEnumerable<PropertyInfo>> IgnoreUpdateProperties = new ConcurrentDictionary<RuntimeTypeHandle, IEnumerable<PropertyInfo>>();
        private static readonly ConcurrentDictionary<RuntimeTypeHandle, PropertyInfo> DefaultSortNameProperty = new ConcurrentDictionary<RuntimeTypeHandle, PropertyInfo>();
        private static readonly ConcurrentDictionary<RuntimeTypeHandle, string> GetQueries = new ConcurrentDictionary<RuntimeTypeHandle, string>();
        private static readonly ConcurrentDictionary<RuntimeTypeHandle, string> TypeTableName = new ConcurrentDictionary<RuntimeTypeHandle, string>();

        private static readonly ISqlAdapter DefaultAdapter = new SqlServerAdapter();
        private static readonly Dictionary<string, ISqlAdapter> AdapterDictionary
            = new Dictionary<string, ISqlAdapter>(6)
            {
                ["sqlconnection"] = new SqlServerAdapter(),
                ["sqlceconnection"] = new SqlCeServerAdapter(),
                ["npgsqlconnection"] = new PostgresAdapter(),
                ["sqliteconnection"] = new SQLiteAdapter(),
                ["mysqlconnection"] = new MySqlAdapter(),
                ["fbconnection"] = new FbAdapter()
            };

        private static List<PropertyInfo> ComputedPropertiesCache(Type type)
        {
            if (ComputedProperties.TryGetValue(type.TypeHandle, out IEnumerable<PropertyInfo> pi))
            {
                return pi.ToList();
            }

            var computedProperties = TypePropertiesCache(type).Where(p => p.GetCustomAttributes(true).Any(a => a is ComputedAttribute)).ToList();

            ComputedProperties[type.TypeHandle] = computedProperties;
            return computedProperties;
        }

        private static List<PropertyInfo> IgnoreUpdatePropertiesCache(Type type)
        {
            if (IgnoreUpdateProperties.TryGetValue(type.TypeHandle, out IEnumerable<PropertyInfo> pi))
            {
                return pi.ToList();
            }

            var ignoreUpdateProperties = TypePropertiesCache(type).Where(p => p.GetCustomAttributes(true).Any(a => a is IgnoreUpdateAttribute)).ToList();

            IgnoreUpdateProperties[type.TypeHandle] = ignoreUpdateProperties;
            return ignoreUpdateProperties;
        }

        private static PropertyInfo DefaultSortNamePropertyCache(Type type)
        {
            if (DefaultSortNameProperty.TryGetValue(type.TypeHandle, out PropertyInfo pi))
            {
                return pi;
            }

            var defaultSortNameProperty = TypePropertiesCache(type).Where(p => p.GetCustomAttributes(true).Any(a => a is DefaultSortNameAttribute)).ToList().FirstOrDefault();
            return defaultSortNameProperty;
        }

        private static List<PropertyInfo> ExplicitKeyPropertiesCache(Type type)
        {
            if (ExplicitKeyProperties.TryGetValue(type.TypeHandle, out IEnumerable<PropertyInfo> pi))
            {
                return pi.ToList();
            }

            var explicitKeyProperties = TypePropertiesCache(type).Where(p => p.GetCustomAttributes(true).Any(a => a is ExplicitKeyAttribute)).ToList();

            ExplicitKeyProperties[type.TypeHandle] = explicitKeyProperties;
            return explicitKeyProperties;
        }

        private static List<PropertyInfo> KeyPropertiesCache(Type type)
        {
            if (KeyProperties.TryGetValue(type.TypeHandle, out IEnumerable<PropertyInfo> pi))
            {
                return pi.ToList();
            }

            var allProperties = TypePropertiesCache(type);
            var keyProperties = allProperties.Where(p => p.GetCustomAttributes(true).Any(a => a is KeyAttribute)).ToList();

            if (keyProperties.Count == 0)
            {
                var idProp = allProperties.Find(p => string.Equals(p.Name, "id", StringComparison.CurrentCultureIgnoreCase));
                if (idProp != null && !idProp.GetCustomAttributes(true).Any(a => a is ExplicitKeyAttribute))
                {
                    keyProperties.Add(idProp);
                }
            }

            KeyProperties[type.TypeHandle] = keyProperties;
            return keyProperties;
        }

        private static List<PropertyInfo> TypePropertiesCache(Type type)
        {
            if (TypeProperties.TryGetValue(type.TypeHandle, out IEnumerable<PropertyInfo> pis))
            {
                return pis.ToList();
            }

            var properties = type.GetProperties().Where(IsWriteable).ToArray();
            TypeProperties[type.TypeHandle] = properties;
            return properties.ToList();
        }

        private static bool IsWriteable(PropertyInfo pi)
        {
            var attributes = pi.GetCustomAttributes(typeof(WriteAttribute), false).AsList();
            if (attributes.Count != 1) return true;

            var writeAttribute = (WriteAttribute)attributes[0];
            return writeAttribute.Write;
        }

        private static PropertyInfo GetSingleKey<T>(string method)
        {
            var type = typeof(T);
            var keys = KeyPropertiesCache(type);
            var explicitKeys = ExplicitKeyPropertiesCache(type);
            var keyCount = keys.Count + explicitKeys.Count;
            if (keyCount > 1)
                throw new DataException($"{method}<T> only supports an entity with a single [Key] or [ExplicitKey] property. [Key] Count: {keys.Count}, [ExplicitKey] Count: {explicitKeys.Count}");
            if (keyCount == 0)
                throw new DataException($"{method}<T> only supports an entity with a [Key] or an [ExplicitKey] property");

            return keys.Count > 0 ? keys[0] : explicitKeys[0];
        }

        private static PropertyInfo GetDefaultSortName<T>(string method)
        {
            var type = typeof(T);
            var keys = KeyPropertiesCache(type);
            var explicitKeys = ExplicitKeyPropertiesCache(type);
            var keyCount = keys.Count + explicitKeys.Count;
            if (keyCount > 1)
                throw new DataException($"{method}<T> only supports an entity with a single [Key] or [ExplicitKey] property. [Key] Count: {keys.Count}, [ExplicitKey] Count: {explicitKeys.Count}");
            if (keyCount == 0)
                throw new DataException($"{method}<T> only supports an entity with a [Key] or an [ExplicitKey] property");

            if (keys.Count > 0)
            {
                return keys[0];
            }
            else if (explicitKeys.Count > 0)
            {
                return DefaultSortNamePropertyCache(type);
            }

            return null;
        }

        /// <summary>
        /// Returns a single entity by a single id from table "Ts".  
        /// Id must be marked with [Key] attribute.
        /// Entities created from interfaces are tracked/intercepted for changes and used by the Update() extension
        /// for optimal performance. 
        /// </summary>
        /// <typeparam name="T">Interface or type to create and populate</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="id">Id of the entity to get, must be marked with [Key] attribute</param>
        /// <param name="transaction">The transaction to run under, null (the default) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <returns>Entity of T</returns>
        public static T Get<T>(this IDbConnection connection, dynamic id, IDbTransaction transaction = null, int? commandTimeout = null) where T : class, new()
        {
            var type = typeof(T);

            if (!GetQueries.TryGetValue(type.TypeHandle, out string sql))
            {
                var property = GetSingleKey<T>(nameof(Get));
                var key = property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name;
                var name = GetTableName(type);

                sql = $"select * from {name} where {key} = @id";
                GetQueries[type.TypeHandle] = sql;
            }

            var dynParams = new DynamicParameters();
            dynParams.Add("@id", id);

            var obj = connection.Query(sql, dynParams, transaction, commandTimeout: commandTimeout).FirstOrDefault();
            return GetImpl<T>(obj, type);
        }

        /// <summary>
        /// Returns an entity from table "Ts".
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="transaction">The transaction to run under, null (the default) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <param name="sql"></param>
        /// <param name="type"></param>
        /// <returns></returns>
        private static T GetImpl<T>(dynamic data, Type type) where T : class, new()
        {
            T obj = new T();

            foreach (var property in TypePropertiesCache(type))
            {
                var name = property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name;
                var res = data as IDictionary<string, object>;
                var val = res[name];
                if (val == null) continue;
                if (property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    var genericType = Nullable.GetUnderlyingType(property.PropertyType);
                    if (genericType != null) property.SetValue(obj, Convert.ChangeType(val, genericType), null);
                }
                else
                {
                    property.SetValue(obj, Convert.ChangeType(val, property.PropertyType), null);
                }
            }

            return obj;
        }

        /// <summary>
        /// Paginated Data
        /// </summary>
        /// <typeparam name="T">Interface type to create and populate</typeparam>
        /// <typeparam name="TModel">TModel</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="filter">Expression</param>
        /// <param name="model"></param>
        /// <param name="defaultSortingColumnName">Default sorting column name</param>
        /// <param name="transaction">The transaction to run under, null (the default) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <param name="sqlAdapter"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public static IEnumerable<T> GetPagedList<T, TModel>(this IDbConnection connection, Expression<Func<T, bool>> filter, TModel model, int page, int itemsPerPage, IDbTransaction transaction = null, int? commandTimeout = null, ISqlAdapter sqlAdapter = null) where T : class, new() where TModel : class, new()
        {
            var type = typeof(T);
            var defaultSortNameProperty = GetDefaultSortName<T>(nameof(GetPagedListAsync));
            var defaultSortName = defaultSortNameProperty.GetCustomAttribute<ColumnAttribute>()?.Name ?? defaultSortNameProperty.Name;
            var cacheType = typeof(List<T>);
            sqlAdapter ??= GetFormatter(connection);
            var name = GetTableName(type);
            FilterResult result = QueryFilter<T, TModel>.BuildWhere(filter, model);

            if (!GetQueries.TryGetValue(cacheType.TypeHandle, out string sql))
            {
                GetSingleKey<T>(nameof(GetPagedList));

                sql = $"SELECT * FROM {name} {result.WhereClause ?? $" WHERE {result.WhereClause}"}";
                GetQueries[cacheType.TypeHandle] = sql;
            }

            // 获取分页记录
            var list = sqlAdapter.RetrieveCurrentPaginatedData(connection, transaction, commandTimeout, name, defaultSortName, page, itemsPerPage, result);

            return GetListImpl<T>(list, type);
        }

        /// <summary>
        /// Count
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="TModel"></typeparam>
        /// <param name="connection"></param>
        /// <param name="filter">Condition expression tree</param>
        /// <param name="model"></param>
        /// <param name="defaultSortingColumnName"></param>
        /// <param name="page"></param>
        /// <param name="itemsPerPage"></param>
        /// <param name="transaction"></param>
        /// <param name="commandTimeout"></param>
        /// <param name="sqlAdapter">The specific ISqlAdapter to use, auto-detected based on connection if null</param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public static int GetCount<T, TModel>(this IDbConnection connection, Expression<Func<T, bool>> filter, TModel model, string defaultSortingColumnName, int page, int itemsPerPage, IDbTransaction transaction = null, int? commandTimeout = null, ISqlAdapter sqlAdapter = null) where T : class, new() where TModel : class, new()
        {
            if (filter == null)
            {
                throw new ArgumentException("'whereExpression' must be a non null.");
            }

            var type = typeof(T);
            var cacheType = typeof(List<T>);
            var name = GetTableName(type);
            FilterResult result = QueryFilter<T, TModel>.BuildWhere(filter, model);

            if (!GetQueries.TryGetValue(cacheType.TypeHandle, out string sql))
            {
                GetSingleKey<T>(nameof(GetCount));

                sql = $"SELECT COUNT(*) FROM {name} {result.WhereClause ?? $" WHERE {result.WhereClause}"}";
                GetQueries[cacheType.TypeHandle] = sql;
            }

            var count = connection.ExecuteScalar(sql, result.Parameters, transaction, commandTimeout);
            return count != null ? (int)count : 0;
        }

        /// <summary>
        /// Returns a list of entities from table "Ts".  
        /// Id of T must be marked with [Key] attribute.
        /// Entities created from interfaces are tracked/intercepted for changes and used by the Update() extension
        /// for optimal performance. 
        /// </summary>
        /// <typeparam name="T">Entity</typeparam>
        /// <typeparam name="TModel">Model</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="filter">Condition expression tree</param>
        /// <param name="model">model</param>
        /// <param name="transaction">The transaction to run under, null (the default) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public static IEnumerable<T> GetList<T, TModel>(this IDbConnection connection, Expression<Func<T, bool>> filter, TModel model, IDbTransaction transaction = null, int? commandTimeout = null)
            where T : class, new()
            where TModel : class, new()
        {
            if (filter == null)
            {
                throw new ArgumentException("'whereExpression' must be a non null.");
            }

            var type = typeof(T);
            var cacheType = typeof(List<T>);
            FilterResult result = QueryFilter<T, TModel>.BuildWhere(filter, model);

            if (!GetQueries.TryGetValue(cacheType.TypeHandle, out string sql))
            {
                GetSingleKey<T>(nameof(GetListAsync));
                var name = GetTableName(type);

                sql = $"SELECT * FROM {name} {result.WhereClause ?? $" WHERE {result.WhereClause}"}";
                GetQueries[cacheType.TypeHandle] = sql;
            }

            var data = connection.Query(sql, param: result.Parameters, transaction: transaction, commandTimeout: commandTimeout);

            return GetListImpl<T>(data, type);
        }

        /// <summary>
        /// Returns a list of entities from table "Ts".
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="transaction">The transaction to run under, null (the default) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <param name="sql"></param>
        /// <param name="type"></param>
        /// <returns></returns>
        private static IEnumerable<T> GetListImpl<T>(IEnumerable<dynamic> data, Type type) where T : class, new()
        {
            var list = new List<T>();

            foreach (IDictionary<string, object> res in data)
            {
                T obj = new T();
                foreach (var property in TypePropertiesCache(type))
                {
                    var name = property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name;
                    var val = res[name];
                    if (val == null) continue;
                    if (property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
                    {
                        var genericType = Nullable.GetUnderlyingType(property.PropertyType);
                        if (genericType != null) property.SetValue(obj, Convert.ChangeType(val, genericType), null);
                    }
                    else
                    {
                        property.SetValue(obj, Convert.ChangeType(val, property.PropertyType), null);
                    }
                }
                list.Add(obj);
            }

            return list;
        }

        /// <summary>
        /// Returns a list of entities from table "Ts".
        /// Id of T must be marked with [Key] attribute.
        /// Entities created from interfaces are tracked/intercepted for changes and used by the Update() extension
        /// for optimal performance.
        /// </summary>
        /// <typeparam name="T">Interface or type to create and populate</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="transaction">The transaction to run under, null (the default) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <returns>Entity of T</returns>
        public static IEnumerable<T> GetAll<T>(this IDbConnection connection, IDbTransaction transaction = null, int? commandTimeout = null) where T : class, new()
        {
            var type = typeof(T);
            var cacheType = typeof(List<T>);

            if (!GetQueries.TryGetValue(cacheType.TypeHandle, out string sql))
            {
                GetSingleKey<T>(nameof(GetAll));
                var name = GetTableName(type);

                sql = "select * from " + name;
                GetQueries[cacheType.TypeHandle] = sql;
            }

            var result = connection.Query(sql);
            return GetListImpl<T>(result, type);
        }

        /// <summary>
        /// Specify a custom table name mapper based on the POCO type name
        /// </summary>
#pragma warning disable CA2211 // Non-constant fields should not be visible - I agree with you, but we can't do that until we break the API
        public static TableNameMapperDelegate TableNameMapper;
#pragma warning restore CA2211 // Non-constant fields should not be visible

        private static string GetTableName(Type type)
        {
            if (TypeTableName.TryGetValue(type.TypeHandle, out string name)) return name;

            if (TableNameMapper != null)
            {
                name = TableNameMapper(type);
            }
            else
            {
                //NOTE: This as dynamic trick falls back to handle both our own Table-attribute as well as the one in EntityFramework 
                var tableAttrName =
                    type.GetCustomAttribute<TableAttribute>(false)?.Name
                    ?? (type.GetCustomAttributes(false).FirstOrDefault(attr => attr.GetType().Name == "TableAttribute") as dynamic)?.Name;

                if (tableAttrName != null)
                {
                    name = tableAttrName;
                }
                else
                {
                    name = type.Name + "s";
                    if (type.IsInterface && name.StartsWith("I"))
                        name = name.Substring(1);
                }
            }

            TypeTableName[type.TypeHandle] = name;
            return name;
        }

        /// <summary>
        /// Inserts an entity into table "Ts" and returns identity id or number of inserted rows if inserting a list.
        /// </summary>
        /// <typeparam name="T">The type to insert.</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="entityToInsert">Entity to insert, can be list of entities</param>
        /// <param name="transaction">The transaction to run under, null (the default) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <returns>Identity of inserted entity, or number of inserted rows if inserting a list</returns>
        public static long Insert<T>(this IDbConnection connection, T entityToInsert, IDbTransaction transaction = null, int? commandTimeout = null) where T : class
        {
            var isList = false;

            var type = typeof(T);

            if (type.IsArray)
            {
                isList = true;
                type = type.GetElementType();
            }
            else if (type.IsGenericType)
            {
                var typeInfo = type.GetTypeInfo();
                bool implementsGenericIEnumerableOrIsGenericIEnumerable =
                    typeInfo.ImplementedInterfaces.Any(ti => ti.IsGenericType && ti.GetGenericTypeDefinition() == typeof(IEnumerable<>)) ||
                    typeInfo.GetGenericTypeDefinition() == typeof(IEnumerable<>);

                if (implementsGenericIEnumerableOrIsGenericIEnumerable)
                {
                    isList = true;
                    type = type.GetGenericArguments()[0];
                }
            }

            var name = GetTableName(type);
            var sbColumnList = new StringBuilder(null);
            var allProperties = TypePropertiesCache(type);
            var keyProperties = KeyPropertiesCache(type);
            var computedProperties = ComputedPropertiesCache(type);
            var allPropertiesExceptKeyAndComputed = allProperties.Except(keyProperties.Union(computedProperties)).ToList();

            var adapter = GetFormatter(connection);

            for (var i = 0; i < allPropertiesExceptKeyAndComputed.Count; i++)
            {
                var property = allPropertiesExceptKeyAndComputed[i];
                string columnName = property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name;
                adapter.AppendColumnName(sbColumnList, columnName);  //fix for issue #336
                if (i < allPropertiesExceptKeyAndComputed.Count - 1)
                    sbColumnList.Append(", ");
            }

            var sbParameterList = new StringBuilder(null);
            for (var i = 0; i < allPropertiesExceptKeyAndComputed.Count; i++)
            {
                var property = allPropertiesExceptKeyAndComputed[i];
                sbParameterList.AppendFormat("@{0}", property.Name);
                if (i < allPropertiesExceptKeyAndComputed.Count - 1)
                    sbParameterList.Append(", ");
            }

            int returnVal;
            var wasClosed = connection.State == ConnectionState.Closed;
            if (wasClosed) connection.Open();

            if (!isList)    //single entity
            {
                returnVal = adapter.Insert(connection, transaction, commandTimeout, name, sbColumnList.ToString(),
                    sbParameterList.ToString(), keyProperties, entityToInsert);
            }
            else
            {
                //insert list of entities
                var cmd = $"insert into {name} ({sbColumnList}) values ({sbParameterList})";
                returnVal = connection.Execute(cmd, entityToInsert, transaction, commandTimeout);
            }
            if (wasClosed) connection.Close();
            return returnVal;
        }

        /// <summary>
        /// Updates entity in table "Ts", checks if the entity is modified if the entity is tracked by the Get() extension.
        /// </summary>
        /// <typeparam name="T">Type to be updated</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="entityToUpdate">Entity to be updated</param>
        /// <param name="transaction">The transaction to run under, null (the default) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <returns>true if updated, false if not found or not modified (tracked entities)</returns>
        public static bool Update<T>(this IDbConnection connection, T entityToUpdate, IDbTransaction transaction = null, int? commandTimeout = null) where T : class
        {
            if (entityToUpdate is IProxy proxy && !proxy.IsDirty)
            {
                return false;
            }

            var type = typeof(T);

            if (type.IsArray)
            {
                type = type.GetElementType();
            }
            else if (type.IsGenericType)
            {
                var typeInfo = type.GetTypeInfo();
                bool implementsGenericIEnumerableOrIsGenericIEnumerable =
                    typeInfo.ImplementedInterfaces.Any(ti => ti.IsGenericType && ti.GetGenericTypeDefinition() == typeof(IEnumerable<>)) ||
                    typeInfo.GetGenericTypeDefinition() == typeof(IEnumerable<>);

                if (implementsGenericIEnumerableOrIsGenericIEnumerable)
                {
                    type = type.GetGenericArguments()[0];
                }
            }

            var keyProperties = KeyPropertiesCache(type).ToList();  //added ToList() due to issue #418, must work on a list copy
            var explicitKeyProperties = ExplicitKeyPropertiesCache(type);
            if (keyProperties.Count == 0 && explicitKeyProperties.Count == 0)
                throw new ArgumentException("Entity must have at least one [Key] or [ExplicitKey] property");

            var name = GetTableName(type);

            var sb = new StringBuilder();
            sb.AppendFormat("update {0} set ", name);

            var allProperties = TypePropertiesCache(type);
            keyProperties.AddRange(explicitKeyProperties);
            var computedProperties = ComputedPropertiesCache(type);
            var ignoreUpdateProperties = IgnoreUpdatePropertiesCache(type);
            var nonIdProps = allProperties.Except(keyProperties.Union(computedProperties).Union(ignoreUpdateProperties)).ToList();

            var adapter = GetFormatter(connection);

            for (var i = 0; i < nonIdProps.Count; i++)
            {
                var property = nonIdProps[i];
                adapter.AppendColumnNameEqualsValue(sb, property);  //fix for issue #336
                if (i < nonIdProps.Count - 1)
                    sb.Append(", ");
            }
            sb.Append(" where ");
            for (var i = 0; i < keyProperties.Count; i++)
            {
                var property = keyProperties[i];
                adapter.AppendColumnNameEqualsValue(sb, property);  //fix for issue #336
                if (i < keyProperties.Count - 1)
                    sb.Append(" and ");
            }
            var updated = connection.Execute(sb.ToString(), entityToUpdate, commandTimeout: commandTimeout, transaction: transaction);
            return updated > 0;
        }

        /// <summary>
        /// Delete entity in table "Ts".
        /// </summary>
        /// <typeparam name="T">Type of entity</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="entityToDelete">Entity to delete</param>
        /// <param name="transaction">The transaction to run under, null (the default) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <returns>true if deleted, false if not found</returns>
        public static bool Delete<T>(this IDbConnection connection, T entityToDelete, IDbTransaction transaction = null, int? commandTimeout = null) where T : class
        {
            if (entityToDelete == null)
                throw new ArgumentException("Cannot Delete null Object", nameof(entityToDelete));

            var type = typeof(T);

            if (type.IsArray)
            {
                type = type.GetElementType();
            }
            else if (type.IsGenericType)
            {
                var typeInfo = type.GetTypeInfo();
                bool implementsGenericIEnumerableOrIsGenericIEnumerable =
                    typeInfo.ImplementedInterfaces.Any(ti => ti.IsGenericType && ti.GetGenericTypeDefinition() == typeof(IEnumerable<>)) ||
                    typeInfo.GetGenericTypeDefinition() == typeof(IEnumerable<>);

                if (implementsGenericIEnumerableOrIsGenericIEnumerable)
                {
                    type = type.GetGenericArguments()[0];
                }
            }

            var keyProperties = KeyPropertiesCache(type).ToList();  //added ToList() due to issue #418, must work on a list copy
            var explicitKeyProperties = ExplicitKeyPropertiesCache(type);
            if (keyProperties.Count == 0 && explicitKeyProperties.Count == 0)
                throw new ArgumentException("Entity must have at least one [Key] or [ExplicitKey] property");

            var name = GetTableName(type);
            keyProperties.AddRange(explicitKeyProperties);

            var sb = new StringBuilder();
            sb.AppendFormat("delete from {0} where ", name);

            var adapter = GetFormatter(connection);

            for (var i = 0; i < keyProperties.Count; i++)
            {
                var property = keyProperties[i];
                adapter.AppendColumnNameEqualsValue(sb, property);  //fix for issue #336
                if (i < keyProperties.Count - 1)
                    sb.Append(" and ");
            }
            var deleted = connection.Execute(sb.ToString(), entityToDelete, transaction, commandTimeout);
            return deleted > 0;
        }

        /// <summary>
        /// Delete all entities in the table related to the type T.
        /// </summary>
        /// <typeparam name="T">Type of entity</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="transaction">The transaction to run under, null (the default) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <returns>true if deleted, false if none found</returns>
        public static bool DeleteAll<T>(this IDbConnection connection, IDbTransaction transaction = null, int? commandTimeout = null) where T : class
        {
            var type = typeof(T);
            var name = GetTableName(type);
            var statement = $"delete from {name}";
            var deleted = connection.Execute(statement, null, transaction, commandTimeout);
            return deleted > 0;
        }

        /// <summary>
        /// Specifies a custom callback that detects the database type instead of relying on the default strategy (the name of the connection type object).
        /// Please note that this callback is global and will be used by all the calls that require a database specific adapter.
        /// </summary>
#pragma warning disable CA2211 // Non-constant fields should not be visible - I agree with you, but we can't do that until we break the API
        public static GetDatabaseTypeDelegate GetDatabaseType;
#pragma warning restore CA2211 // Non-constant fields should not be visible

        private static ISqlAdapter GetFormatter(IDbConnection connection)
        {
            var name = GetDatabaseType?.Invoke(connection).ToLower()
                       ?? connection.GetType().Name.ToLower();

            return AdapterDictionary.TryGetValue(name, out var adapter)
                ? adapter
                : DefaultAdapter;
        }

        private static class ProxyGenerator
        {
            private static readonly Dictionary<Type, Type> TypeCache = new Dictionary<Type, Type>();

            private static AssemblyBuilder GetAsmBuilder(string name)
            {
#if !NET461
                return AssemblyBuilder.DefineDynamicAssembly(new AssemblyName { Name = name }, AssemblyBuilderAccess.Run);
#else
                return Thread.GetDomain().DefineDynamicAssembly(new AssemblyName { Name = name }, AssemblyBuilderAccess.Run);
#endif
            }

            public static T GetInterfaceProxy<T>()
            {
                Type typeOfT = typeof(T);

                if (TypeCache.TryGetValue(typeOfT, out Type k))
                {
                    return (T)Activator.CreateInstance(k);
                }
                var assemblyBuilder = GetAsmBuilder(typeOfT.Name);

                var moduleBuilder = assemblyBuilder.DefineDynamicModule("SqlMapperExtensions." + typeOfT.Name); //NOTE: to save, add "asdasd.dll" parameter

                var interfaceType = typeof(IProxy);
                var typeBuilder = moduleBuilder.DefineType(typeOfT.Name + "_" + Guid.NewGuid(),
                    TypeAttributes.Public | TypeAttributes.Class);
                typeBuilder.AddInterfaceImplementation(typeOfT);
                typeBuilder.AddInterfaceImplementation(interfaceType);

                //create our _isDirty field, which implements IProxy
                var setIsDirtyMethod = CreateIsDirtyProperty(typeBuilder);

                // Generate a field for each property, which implements the T
                foreach (var property in typeof(T).GetProperties())
                {
                    var isId = property.GetCustomAttributes(true).Any(a => a is KeyAttribute);
                    CreateProperty<T>(typeBuilder, property.Name, property.PropertyType, setIsDirtyMethod, isId);
                }

#if NETSTANDARD2_0
                var generatedType = typeBuilder.CreateTypeInfo().AsType();
#else
                var generatedType = typeBuilder.CreateType();
#endif

                TypeCache.Add(typeOfT, generatedType);
                return (T)Activator.CreateInstance(generatedType);
            }

            private static MethodInfo CreateIsDirtyProperty(TypeBuilder typeBuilder)
            {
                var propType = typeof(bool);
                var field = typeBuilder.DefineField("_" + nameof(IProxy.IsDirty), propType, FieldAttributes.Private);
                var property = typeBuilder.DefineProperty(nameof(IProxy.IsDirty),
                                               System.Reflection.PropertyAttributes.None,
                                               propType,
                                               new[] { propType });

                const MethodAttributes getSetAttr = MethodAttributes.Public | MethodAttributes.NewSlot | MethodAttributes.SpecialName
                                                  | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.HideBySig;

                // Define the "get" and "set" accessor methods
                var currGetPropMthdBldr = typeBuilder.DefineMethod("get_" + nameof(IProxy.IsDirty),
                                             getSetAttr,
                                             propType,
                                             Type.EmptyTypes);
                var currGetIl = currGetPropMthdBldr.GetILGenerator();
                currGetIl.Emit(OpCodes.Ldarg_0);
                currGetIl.Emit(OpCodes.Ldfld, field);
                currGetIl.Emit(OpCodes.Ret);
                var currSetPropMthdBldr = typeBuilder.DefineMethod("set_" + nameof(IProxy.IsDirty),
                                             getSetAttr,
                                             null,
                                             new[] { propType });
                var currSetIl = currSetPropMthdBldr.GetILGenerator();
                currSetIl.Emit(OpCodes.Ldarg_0);
                currSetIl.Emit(OpCodes.Ldarg_1);
                currSetIl.Emit(OpCodes.Stfld, field);
                currSetIl.Emit(OpCodes.Ret);

                property.SetGetMethod(currGetPropMthdBldr);
                property.SetSetMethod(currSetPropMthdBldr);
                var getMethod = typeof(IProxy).GetMethod("get_" + nameof(IProxy.IsDirty));
                var setMethod = typeof(IProxy).GetMethod("set_" + nameof(IProxy.IsDirty));
                typeBuilder.DefineMethodOverride(currGetPropMthdBldr, getMethod);
                typeBuilder.DefineMethodOverride(currSetPropMthdBldr, setMethod);

                return currSetPropMthdBldr;
            }

            private static void CreateProperty<T>(TypeBuilder typeBuilder, string propertyName, Type propType, MethodInfo setIsDirtyMethod, bool isIdentity)
            {
                //Define the field and the property 
                var field = typeBuilder.DefineField("_" + propertyName, propType, FieldAttributes.Private);
                var property = typeBuilder.DefineProperty(propertyName,
                                               System.Reflection.PropertyAttributes.None,
                                               propType,
                                               new[] { propType });

                const MethodAttributes getSetAttr = MethodAttributes.Public
                                                    | MethodAttributes.Virtual
                                                    | MethodAttributes.HideBySig;

                // Define the "get" and "set" accessor methods
                var currGetPropMthdBldr = typeBuilder.DefineMethod("get_" + propertyName,
                                             getSetAttr,
                                             propType,
                                             Type.EmptyTypes);

                var currGetIl = currGetPropMthdBldr.GetILGenerator();
                currGetIl.Emit(OpCodes.Ldarg_0);
                currGetIl.Emit(OpCodes.Ldfld, field);
                currGetIl.Emit(OpCodes.Ret);

                var currSetPropMthdBldr = typeBuilder.DefineMethod("set_" + propertyName,
                                             getSetAttr,
                                             null,
                                             new[] { propType });

                //store value in private field and set the isdirty flag
                var currSetIl = currSetPropMthdBldr.GetILGenerator();
                currSetIl.Emit(OpCodes.Ldarg_0);
                currSetIl.Emit(OpCodes.Ldarg_1);
                currSetIl.Emit(OpCodes.Stfld, field);
                currSetIl.Emit(OpCodes.Ldarg_0);
                currSetIl.Emit(OpCodes.Ldc_I4_1);
                currSetIl.Emit(OpCodes.Call, setIsDirtyMethod);
                currSetIl.Emit(OpCodes.Ret);

                //TODO: Should copy all attributes defined by the interface?
                if (isIdentity)
                {
                    var keyAttribute = typeof(KeyAttribute);
                    var myConstructorInfo = keyAttribute.GetConstructor(Type.EmptyTypes);
                    var attributeBuilder = new CustomAttributeBuilder(myConstructorInfo, Array.Empty<object>());
                    property.SetCustomAttribute(attributeBuilder);
                }

                property.SetGetMethod(currGetPropMthdBldr);
                property.SetSetMethod(currSetPropMthdBldr);
                var getMethod = typeof(T).GetMethod("get_" + propertyName);
                var setMethod = typeof(T).GetMethod("set_" + propertyName);
                typeBuilder.DefineMethodOverride(currGetPropMthdBldr, getMethod);
                typeBuilder.DefineMethodOverride(currSetPropMthdBldr, setMethod);
            }
        }
    }

    /// <summary>
    /// Reflection Utility
    /// </summary>
    public static class ReflectionUtil
    {
        public static object GetPropertyValue(object obj, string propertyName, int? index = null)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            if (string.IsNullOrEmpty(propertyName)) throw new ArgumentNullException(nameof(propertyName));

            Type type = obj.GetType();
            PropertyInfo propertyInfo = type.GetProperty(propertyName);
            if (propertyInfo == null)
                throw new ArgumentException($"Property '{propertyName}' not found on type '{type.FullName}'");

            object value = propertyInfo.GetValue(obj);

            // 判断是否为 List 类型
            if (value is IEnumerable enumerable && value.GetType().IsGenericType)
            {
                var genericType = value.GetType().GetGenericTypeDefinition();
                if (genericType == typeof(List<>))
                {
                    if (index.HasValue)
                    {
                        // 按索引取值（类似 Java 的 get(index)）
                        dynamic list = value;
                        if (index >= 0 && index < list.Count)
                            return list[index.Value];
                        else
                            throw new ArgumentOutOfRangeException(nameof(index), "Index out of range");
                    }
                    return value; // 返回整个 List
                }
            }

            return value;
        }
    }

    /// <summary>
    /// Generate Sql from Expression SQL
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="TModel"></typeparam>
    public static class QueryFilter<T, TModel> where T : class, new() where TModel : class, new()
    {
        public static dynamic BuildWhere(Expression<Func<T, bool>> expression, TModel model)
        {
            if (expression == null)
            {
                return new FilterResult { WhereClause = null, Parameters = null };
            }

            var conditions = new List<string>();
            DynamicParameters parameters = new DynamicParameters();
            Visit(expression.Body, conditions, parameters, model);

            string? where = conditions.Any() ? string.Join(" ", conditions) : null;
            return new FilterResult { WhereClause = where, Parameters = parameters };
        }

        private static void Visit(Expression expression, List<string> conditions, DynamicParameters parameters, TModel model)
        {
            switch (expression.NodeType)
            {
                case ExpressionType.Lambda:
                    KeyValuePair<string, string> memberName = new KeyValuePair<string, string>();
                    Visit(((LambdaExpression)expression).Body, conditions, parameters, model);
                    break;

                case ExpressionType.AndAlso:
                    var binaryAnd = (BinaryExpression)expression;
                    Visit(binaryAnd.Left, conditions, parameters, model);
                    conditions.Add($"AND");
                    Visit(binaryAnd.Right, conditions, parameters, model);
                    break;

                case ExpressionType.OrElse:
                    var binaryOr = (BinaryExpression)expression;

                    conditions.Add("(");
                    Visit(binaryOr.Left, conditions, parameters, model);
                    conditions.Add($"OR");
                    Visit(binaryOr.Right, conditions, parameters, model);
                    conditions.Add(")");
                    break;

                case ExpressionType.Equal:
                    var binaryEqual = (BinaryExpression)expression;
                    memberName = GetMemberName(binaryEqual.Left);

                    if (!string.IsNullOrWhiteSpace(ReflectionUtil.GetPropertyValue(model, memberName.Key)?.ToString()))
                    {
                        conditions.Add($"{memberName.Value} = {GetValue(binaryEqual.Right, parameters, model)}");
                    }
                    else
                    {
                        conditions.Add("1=1");
                    }

                    break;

                case ExpressionType.GreaterThan:
                    var binaryGreaterThan = (BinaryExpression)expression;
                    memberName = GetMemberName(binaryGreaterThan.Left);

                    if (!string.IsNullOrWhiteSpace(ReflectionUtil.GetPropertyValue(model, memberName.Key)?.ToString()))
                    {
                        conditions.Add($"{memberName.Value} > {GetValue(binaryGreaterThan.Right, parameters, model)}");
                    }
                    else
                    {
                        conditions.Add("1=1");
                    }

                    break;

                case ExpressionType.GreaterThanOrEqual:
                    var binaryGreaterThanOrEqual = (BinaryExpression)expression;
                    memberName = GetMemberName(binaryGreaterThanOrEqual.Left);

                    if (!string.IsNullOrWhiteSpace(ReflectionUtil.GetPropertyValue(model, memberName.Key)?.ToString()))
                    {
                        conditions.Add($"{memberName.Value} >= {GetValue(binaryGreaterThanOrEqual.Right, parameters, model)}");
                    }
                    else
                    {
                        conditions.Add("1=1");
                    }

                    break;

                case ExpressionType.LessThan:
                    var binaryLessThan = (BinaryExpression)expression;
                    memberName = GetMemberName(binaryLessThan.Left);

                    if (!string.IsNullOrWhiteSpace(ReflectionUtil.GetPropertyValue(model, memberName.Key)?.ToString()))
                    {
                        conditions.Add($"{memberName.Value} < {GetValue(binaryLessThan.Right, parameters, model)}");
                    }
                    else
                    {
                        conditions.Add("1=1");
                    }

                    break;

                case ExpressionType.LessThanOrEqual:
                    var binaryLessThanOrEqual = (BinaryExpression)expression;
                    memberName = GetMemberName(binaryLessThanOrEqual.Left);

                    if (!string.IsNullOrWhiteSpace(ReflectionUtil.GetPropertyValue(model, memberName.Key)?.ToString()))
                    {
                        conditions.Add($"{memberName.Value} <= {GetValue(binaryLessThanOrEqual.Right, parameters, model)}");
                    }
                    else
                    {
                        conditions.Add("1=1");
                    }

                    break;

                case ExpressionType.Call:
                    var methodCall = (MethodCallExpression)expression;
                    if (methodCall.Method.Name == "StartsWith")
                    {
                        memberName = GetMemberName(methodCall.Object);

                        if (!string.IsNullOrWhiteSpace(ReflectionUtil.GetPropertyValue(model, memberName.Key)?.ToString()))
                        {
                            conditions.Add($"{memberName.Value} LIKE {GetValue(methodCall.Arguments[0], parameters, model)} + '%'");
                        }
                        else
                        {
                            conditions.Add("1=1");
                        }
                    }
                    else if (methodCall.Method.Name == "EndsWith")
                    {
                        memberName = GetMemberName(methodCall.Object);

                        if (!string.IsNullOrWhiteSpace(ReflectionUtil.GetPropertyValue(model, memberName.Key)?.ToString()))
                        {
                            conditions.Add($"{memberName.Value} LIKE '%' + {GetValue(methodCall.Arguments[0], parameters, model)}");
                        }
                        else
                        {
                            conditions.Add("1=1");
                        }
                    }
                    else if (methodCall.Method.Name == "Equals")
                    {
                        memberName = GetMemberName(methodCall.Object);

                        if (!string.IsNullOrWhiteSpace(ReflectionUtil.GetPropertyValue(model, memberName.Key)?.ToString()))
                        {
                            conditions.Add($"{memberName.Value} = {GetValue(methodCall.Arguments[0], parameters, model)}");
                        }
                        else
                        {
                            conditions.Add("1=1");
                        }
                    }
                    else if (methodCall.Method.Name == "Contains")
                    {
                        memberName = GetMemberName(methodCall.Arguments[0]);

                        if (methodCall.Method.DeclaringType == typeof(List<string>))
                        {
                            conditions.Add($"{memberName.Value} IN ({GetValue(methodCall.Object, parameters, model)})");
                        }
                        else if (!string.IsNullOrWhiteSpace(ReflectionUtil.GetPropertyValue(model, memberName.Key)?.ToString()))
                        {
                            conditions.Add($"{memberName.Value} LIKE CONCAT('%', {GetValue(methodCall.Object, parameters, model)} ,'%')");
                        }
                        else
                        {
                            conditions.Add("1=1");
                        }

                    }
                    else if (methodCall.Method.Name == "IN")
                    {

                        memberName = GetMemberName(methodCall.Arguments[0]);

                        if (!string.IsNullOrWhiteSpace(ReflectionUtil.GetPropertyValue(model, memberName.Key)?.ToString()))
                        {
                            conditions.Add($"{memberName.Value} IN ({GetValue(methodCall.Object, parameters, model)})");
                        }
                        else
                        {
                            conditions.Add("1=1");
                        }
                    }
                    break;

                case ExpressionType.Not:
                    if (expression is UnaryExpression unary && unary.Operand is MethodCallExpression notContains)
                    {
                        if (notContains.Method.Name == "Contains")
                        {
                            memberName = GetMemberName(notContains.Arguments[0]);

                            if (!string.IsNullOrWhiteSpace(ReflectionUtil.GetPropertyValue(model, memberName.Key)?.ToString()))
                            {
                                conditions.Add($"{memberName.Value} NOT IN ({GetValue(notContains.Object, parameters, model)})");
                            }
                            else
                            {
                                conditions.Add("1=1");
                            }
                        }
                    }
                    break;

                case ExpressionType.Convert:
                case ExpressionType.TypeAs:
                    Visit(((UnaryExpression)expression).Operand, conditions, parameters, model);
                    break;

                default:
                    throw new NotSupportedException($"Unsupported expression type: {expression.NodeType}");
            }
        }

        private static KeyValuePair<string, string> GetMemberName(Expression expression)
        {
            string? name;
            switch (expression)
            {
                case MemberExpression member:
                    name = member.Member.GetCustomAttribute<ColumnAttribute>()?.Name ?? member.Member.Name;
                    return new KeyValuePair<string, string>(member.Member.Name, name);

                case UnaryExpression unary when unary.Operand is MemberExpression:
                    name = ((MemberExpression)unary.Operand).Member.GetCustomAttribute<ColumnAttribute>()?.Name ?? ((MemberExpression)unary.Operand).Member.Name;
                    return new KeyValuePair<string, string>(((MemberExpression)unary.Operand).Member.Name, name);

                case BinaryExpression binary when binary.NodeType == ExpressionType.OrElse:
                    return GetMemberName(binary.Left);

                case BinaryExpression binary when binary.NodeType == ExpressionType.Call:
                    return GetMemberName(binary.Left);

                case BinaryExpression binary when binary.NodeType == ExpressionType.Equal:
                    return GetMemberName(binary.Left);

                case BinaryExpression binary when binary.NodeType == ExpressionType.GreaterThan:
                    return GetMemberName(binary.Left);

                case BinaryExpression binary when binary.NodeType == ExpressionType.GreaterThanOrEqual:
                    return GetMemberName(binary.Left);

                case BinaryExpression binary when binary.NodeType == ExpressionType.LessThan:
                    return GetMemberName(binary.Left);

                case BinaryExpression binary when binary.NodeType == ExpressionType.LessThanOrEqual:
                    return GetMemberName(binary.Left);

                case BinaryExpression binary when binary.NodeType == ExpressionType.Coalesce:
                    return GetMemberName(binary.Left);

                default:
                    throw new NotSupportedException($"Unsupported expression type for member name: {expression.NodeType}");
            }
        }

        private static object GetValue(Expression expression, DynamicParameters parameters, TModel model)
        {
            switch (expression)
            {
                case ConstantExpression constant:
                    return $"'{constant.Value}'";

                case MemberExpression member:
                    var result = ReflectionUtil.GetPropertyValue(model, member.Member.Name);

                    if (result is IEnumerable<dynamic> list)
                    {
                        parameters.Add($"@{member.Member.Name}", list);
                    }
                    else
                    {
                        parameters.Add($"@{member.Member.Name}", result);
                        Console.WriteLine($"{member.Member.Name}: {ReflectionUtil.GetPropertyValue(model, member.Member.Name)}");
                    }

                    return $"@{member.Member.Name}";

                case BinaryExpression binary:
                    // For the 'BinaryExpression'，we need read the right value.
                    return GetValue(binary.Right, parameters, model);

                case UnaryExpression unary when unary.NodeType == ExpressionType.Convert:
                    // Delivery processing 'Convert' node
                    return GetValue(unary.Operand, parameters, model);

                default:
                    throw new NotSupportedException($"Unsupported expression type for value extraction: {expression.NodeType}");
            }
        }
    }

    /// <summary>
    /// Filter Result
    /// </summary>
    public class FilterResult
    {
        /// <summary>
        /// Where
        /// </summary>
        public string? WhereClause { get; set; }

        /// <summary>
        /// Dynamic Paramters
        /// </summary>
        public DynamicParameters? Parameters { get; set; }
    }

    /// <summary>
    /// Defines the name of a table to use in Dapper.Contrib commands.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class TableAttribute : Attribute
    {
        /// <summary>
        /// Creates a table mapping to a specific name for Dapper.Contrib commands
        /// </summary>
        /// <param name="tableName">The name of this table in the database.</param>
        public TableAttribute(string tableName)
        {
            Name = tableName;
        }

        /// <summary>
        /// The name of the table in the database
        /// </summary>
        public string Name { get; set; }
    }

    /// <summary>
    /// Specifies that this field is a primary key in the database
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class KeyAttribute : Attribute
    {
    }

    /// <summary>
    /// Specifies that this field is an explicitly set primary key in the database
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class ExplicitKeyAttribute : Attribute
    {
    }

    /// <summary>
    /// Specifies whether a field is writable in the database.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class WriteAttribute : Attribute
    {
        /// <summary>
        /// Specifies whether a field is writable in the database.
        /// </summary>
        /// <param name="write">Whether a field is writable in the database.</param>
        public WriteAttribute(bool write)
        {
            Write = write;
        }

        /// <summary>
        /// Whether a field is writable in the database.
        /// </summary>
        public bool Write { get; }
    }

    /// <summary>
    /// Specifies that this is a computed column.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class ComputedAttribute : Attribute
    {
    }

    /// <summary>
    /// Specifies that this is a ignored column
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class IgnoreUpdateAttribute : Attribute
    {
    }

    /// <summary>
    /// Map to the name of column name to use in Dapper.Contrib commands.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class ColumnAttribute : Attribute
    {
        /// <summary>
        /// Creates a field mapping to a specific name for Dapper.Contrib commands.
        /// </summary>
        /// <param name="name">The name of a table's column in the database</param>
        public ColumnAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; set; }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class DefaultSortNameAttribute : Attribute
    {
        
    }
}

/// <summary>
/// The interface for all Dapper.Contrib database operations
/// Implementing this is each provider's model.
/// </summary>
public partial interface ISqlAdapter
{
    /// <summary>
    /// Inserts <paramref name="entityToInsert"/> into the database, returning the Id of the row created.
    /// </summary>
    /// <param name="connection">The connection to use.</param>
    /// <param name="transaction">The transaction to use.</param>
    /// <param name="commandTimeout">The command timeout to use.</param>
    /// <param name="tableName">The table to insert into.</param>
    /// <param name="columnList">The columns to set with this insert.</param>
    /// <param name="parameterList">The parameters to set for this insert.</param>
    /// <param name="keyProperties">The key columns in this table.</param>
    /// <param name="entityToInsert">The entity to insert.</param>
    /// <returns>The Id of the row created.</returns>
    int Insert(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert);

    /// <summary>
    /// Adds the name of a column.
    /// </summary>
    /// <param name="sb">The string builder  to append to.</param>
    /// <param name="columnName">The column name.</param>
    void AppendColumnName(StringBuilder sb, string columnName);
    /// <summary>
    /// Adds a column equality to a parameter.
    /// </summary>
    /// <param name="sb">The string builder  to append to.</param>
    /// <param name="columnName">The column name.</param>
    void AppendColumnNameEqualsValue(StringBuilder sb, PropertyInfo property);

    /// <summary>
    /// Retrieve the current paginated data based on the sorted column names.
    /// </summary>
    /// <returns></returns>
    IEnumerable<dynamic> RetrieveCurrentPaginatedData(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string sortingColumnName, int page, int itemsPerPage, FilterResult result);
}

/// <summary>
/// The SQL Server database adapter.
/// </summary>
public partial class SqlServerAdapter : ISqlAdapter
{
    /// <summary>
    /// Inserts <paramref name="entityToInsert"/> into the database, returning the Id of the row created.
    /// </summary>
    /// <param name="connection">The connection to use.</param>
    /// <param name="transaction">The transaction to use.</param>
    /// <param name="commandTimeout">The command timeout to use.</param>
    /// <param name="tableName">The table to insert into.</param>
    /// <param name="columnList">The columns to set with this insert.</param>
    /// <param name="parameterList">The parameters to set for this insert.</param>
    /// <param name="keyProperties">The key columns in this table.</param>
    /// <param name="entityToInsert">The entity to insert.</param>
    /// <returns>The Id of the row created.</returns>
    public int Insert(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var cmd = $"insert into {tableName} ({columnList}) values ({parameterList});select SCOPE_IDENTITY() id";
        var multi = connection.QueryMultiple(cmd, entityToInsert, transaction, commandTimeout);

        if (keyProperties.Any())
        {
            var first = multi.Read().FirstOrDefault();
            if (first == null || first.id == null) return 0;

            var id = (int)first.id;
            var propertyInfos = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
            if (propertyInfos.Length == 0) return id;

            var idProperty = propertyInfos[0];
            idProperty.SetValue(entityToInsert, Convert.ChangeType(id, idProperty.PropertyType), null);

            return id;
        }

        var result = multi.Read().Count();

        return result;
    }

    /// <summary>
    /// Retrieve current paginated data
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="connection"></param>
    /// <param name="transaction"></param>
    /// <param name="commandTimeout"></param>
    /// <param name="tableName">The table to insert into.</param>
    /// <param name="sortingColumnName">Sorting column name, such as timestamp or auto-increment column</param>
    /// <param name="page">Current page index</param>
    /// <param name="itemsPerPage">Items for per page</param>
    /// <param name="result"></param>
    /// <returns></returns>
    public IEnumerable<dynamic> RetrieveCurrentPaginatedData(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string sortingColumnName, int page, int itemsPerPage, FilterResult result)
    {
        string cmd = $"SELECT MIN({sortingColumnName}) FROM (SELECT {sortingColumnName} FROM {tableName} WHERE {result.WhereClause ?? "1=1"} ORDER BY {sortingColumnName} DESC OFFSET {(page - 1) * itemsPerPage} ROWS FETCH NEXT {itemsPerPage} ROWS ONLY) AS t";
        var data = connection.ExecuteScalar<long>(cmd, result.Parameters);

        if (data > 0)
        {
            var list = connection.Query($"SELECT * FROM {tableName} WHERE {result.WhereClause ?? "1=1"} AND {sortingColumnName} >= {data} ORDER BY {sortingColumnName} DESC OFFSET {(page - 1) * itemsPerPage} ROWS FETCH NEXT {itemsPerPage} ROWS ONLY;", result.Parameters);
            return list;
        }

        return new List<dynamic>();
    }

    /// <summary>
    /// Adds the name of a column.
    /// </summary>
    /// <param name="sb">The string builder  to append to.</param>
    /// <param name="columnName">The column name.</param>
    public void AppendColumnName(StringBuilder sb, string columnName)
    {
        sb.AppendFormat("[{0}]", columnName);
    }

    /// <summary>
    /// Adds a column equality to a parameter.
    /// </summary>
    /// <param name="sb">The string builder  to append to.</param>
    /// <param name="columnName">The column name.</param>
    public void AppendColumnNameEqualsValue(StringBuilder sb, PropertyInfo property)
    {
        sb.AppendFormat("[{0}] = @{1}", property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name, property.Name);
    }
}

/// <summary>
/// The SQL Server Compact Edition database adapter.
/// </summary>
public partial class SqlCeServerAdapter : ISqlAdapter
{
    /// <summary>
    /// Inserts <paramref name="entityToInsert"/> into the database, returning the Id of the row created.
    /// </summary>
    /// <param name="connection">The connection to use.</param>
    /// <param name="transaction">The transaction to use.</param>
    /// <param name="commandTimeout">The command timeout to use.</param>
    /// <param name="tableName">The table to insert into.</param>
    /// <param name="columnList">The columns to set with this insert.</param>
    /// <param name="parameterList">The parameters to set for this insert.</param>
    /// <param name="keyProperties">The key columns in this table.</param>
    /// <param name="entityToInsert">The entity to insert.</param>
    /// <returns>The Id of the row created.</returns>
    public int Insert(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var cmd = $"insert into {tableName} ({columnList}) values ({parameterList})";
        var result = connection.Execute(cmd, entityToInsert, transaction, commandTimeout);

        if (keyProperties.Any() && result > 0)
        {
            var r = connection.Query("select @@IDENTITY id", transaction: transaction, commandTimeout: commandTimeout).ToList();

            if (r[0].id == null) return 0;
            var id = (int)r[0].id;

            var propertyInfos = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
            if (propertyInfos.Length == 0) return id;

            var idProperty = propertyInfos[0];
            idProperty.SetValue(entityToInsert, Convert.ChangeType(id, idProperty.PropertyType), null);

            return id;
        }

        return result;
    }

    /// <summary>
    /// Retrieve current paginated data
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="connection"></param>
    /// <param name="transaction"></param>
    /// <param name="commandTimeout"></param>
    /// <param name="tableName">The table to insert into.</param>
    /// <param name="sortingColumnName">Sorting column name, such as timestamp or auto-increment column</param>
    /// <param name="page">Current page index</param>
    /// <param name="itemsPerPage">Items for per page</param>
    /// <param name="result"></param>
    /// <returns></returns>
    public IEnumerable<dynamic> RetrieveCurrentPaginatedData(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string sortingColumnName, int page, int itemsPerPage, FilterResult result)
    {
        string cmd = $"SELECT MIN({sortingColumnName} FROM (SELECT {sortingColumnName}, ROW_NUMBER() OVER (ORDER BY {sortingColumnName} DESC) AS row_num FROM {tableName} WHERE {result.WhereClause ?? "1=1"}) AS t WHERE row_num BETWEEN {(page - 1) * itemsPerPage + 1} AND {page * itemsPerPage}";
        var data = connection.ExecuteScalar<long>(cmd, result.Parameters);

        if (data > 0)
        {
            var list = connection.Query($"SELECT * FROM {tableName}  WHERE {result.WhereClause ?? "1=1"} AND {sortingColumnName} >= {data} ORDER BY {sortingColumnName} DESC;", result.Parameters);
            return list;
        }

        return new List<dynamic>();
    }

    /// <summary>
    /// Adds the name of a column.
    /// </summary>
    /// <param name="sb">The string builder  to append to.</param>
    /// <param name="columnName">The column name.</param>
    public void AppendColumnName(StringBuilder sb, string columnName)
    {
        sb.AppendFormat("[{0}]", columnName);
    }

    /// <summary>
    /// Adds a column equality to a parameter.
    /// </summary>
    /// <param name="sb">The string builder  to append to.</param>
    /// <param name="columnName">The column name.</param>
    public void AppendColumnNameEqualsValue(StringBuilder sb, PropertyInfo property)
    {
        sb.AppendFormat("[{0}] = @{1}", property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name, property.Name);
    }
}

/// <summary>
/// The MySQL database adapter.
/// </summary>
public partial class MySqlAdapter : ISqlAdapter
{
    /// <summary>
    /// Inserts <paramref name="entityToInsert"/> into the database, returning the Id of the row created.
    /// </summary>
    /// <param name="connection">The connection to use.</param>
    /// <param name="transaction">The transaction to use.</param>
    /// <param name="commandTimeout">The command timeout to use.</param>
    /// <param name="tableName">The table to insert into.</param>
    /// <param name="columnList">The columns to set with this insert.</param>
    /// <param name="parameterList">The parameters to set for this insert.</param>
    /// <param name="keyProperties">The key columns in this table.</param>
    /// <param name="entityToInsert">The entity to insert.</param>
    /// <returns>The Id of the row created.</returns>
    public int Insert(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var cmd = $"INSERT INTO {tableName} ({columnList}) VALUES ({parameterList})";
        var result = connection.Execute(cmd, entityToInsert, transaction, commandTimeout);

        if (keyProperties.Any() && result > 0)
        {
            var r = connection.Query("Select LAST_INSERT_ID() id", transaction: transaction, commandTimeout: commandTimeout);

            var id = r.First().id;
            if (id == null) return 0;
            var propertyInfos = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
            if (propertyInfos.Length == 0) return Convert.ToInt32(id);

            var idp = propertyInfos[0];
            idp.SetValue(entityToInsert, Convert.ChangeType(id, idp.PropertyType), null);

            return Convert.ToInt32(id);
        }

        return result;
    }

    /// <summary>
    /// Retrieve current paginated data
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="connection"></param>
    /// <param name="transaction"></param>
    /// <param name="commandTimeout"></param>
    /// <param name="tableName">The table to insert into.</param>
    /// <param name="sortingColumnName">Sorting column name, such as timestamp or auto-increment column</param>
    /// <param name="page">Current page index</param>
    /// <param name="itemsPerPage">Items for per page</param>
    /// <param name="result"></param>
    /// <returns></returns>
    public IEnumerable<dynamic> RetrieveCurrentPaginatedData(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string sortingColumnName, int page, int itemsPerPage, FilterResult result)
    {
        string cmd = $"SELECT MIN(`{sortingColumnName}`) FROM {tableName} WHERE {result.WhereClause ?? "1=1"} ORDER BY `{sortingColumnName}` DESC LIMIT {(page - 1) * itemsPerPage}, {itemsPerPage}";
        var data = connection.ExecuteScalar<long>(cmd, result.Parameters);

        if (data > 0)
        {
            var list = connection.Query($"SELECT * FROM {tableName} WHERE {result.WhereClause ?? "1=1"} AND {sortingColumnName} >= {data} ORDER BY {sortingColumnName} DESC LIMIT {(page - 1) * itemsPerPage}, {itemsPerPage}", result.Parameters);
            return list;
        }

        return new List<dynamic>();
    }

    /// <summary>
    /// Adds the name of a column.
    /// </summary>
    /// <param name="sb">The string builder  to append to.</param>
    /// <param name="columnName">The column name.</param>
    public void AppendColumnName(StringBuilder sb, string columnName)
    {
        sb.AppendFormat("`{0}`", columnName);
    }

    /// <summary>
    /// Adds a column equality to a parameter.
    /// </summary>
    /// <param name="sb">The string builder  to append to.</param>
    /// <param name="columnName">The column name.</param>
    public void AppendColumnNameEqualsValue(StringBuilder sb, PropertyInfo property)
    {
        sb.AppendFormat("`{0}` = @{1}", property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name, property.Name);
    }
}

/// <summary>
/// The Postgres database adapter.
/// </summary>
public partial class PostgresAdapter : ISqlAdapter
{
    /// <summary>
    /// Inserts <paramref name="entityToInsert"/> into the database, returning the Id of the row created.
    /// </summary>
    /// <param name="connection">The connection to use.</param>
    /// <param name="transaction">The transaction to use.</param>
    /// <param name="commandTimeout">The command timeout to use.</param>
    /// <param name="tableName">The table to insert into.</param>
    /// <param name="columnList">The columns to set with this insert.</param>
    /// <param name="parameterList">The parameters to set for this insert.</param>
    /// <param name="keyProperties">The key columns in this table.</param>
    /// <param name="entityToInsert">The entity to insert.</param>
    /// <returns>The Id of the row created.</returns>
    public int Insert(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var sb = new StringBuilder();
        sb.AppendFormat("insert into {0} ({1}) values ({2})", tableName, columnList, parameterList);

        // If no primary key then safe to assume a join table with not too much data to return
        var propertyInfos = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
        if (propertyInfos.Length == 0)
        {
            sb.Append(" RETURNING *");
        }
        else
        {
            sb.Append(" RETURNING ");
            var first = true;
            foreach (var property in propertyInfos)
            {
                if (!first)
                    sb.Append(", ");
                first = false;
                sb.Append(property.Name);
            }
        }

        var results = connection.Query(sb.ToString(), entityToInsert, transaction, commandTimeout: commandTimeout).ToList();

        if (keyProperties.Any())
        {
            // Return the key by assigning the corresponding property in the object - by product is that it supports compound primary keys
            var id = 0;
            foreach (var p in propertyInfos)
            {
                var value = ((IDictionary<string, object>)results[0])[p.Name.ToLower()];
                p.SetValue(entityToInsert, value, null);
                if (id == 0)
                    id = Convert.ToInt32(value);
            }
            return id;
        }

        return results.Count;
    }

    /// <summary>
    /// Retrieve current paginated data
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="connection"></param>
    /// <param name="transaction"></param>
    /// <param name="commandTimeout"></param>
    /// <param name="tableName">The table to insert into.</param>
    /// <param name="sortingColumnName">Sorting column name, such as timestamp or auto-increment column</param>
    /// <param name="page">Current page index</param>
    /// <param name="itemsPerPage">Items for per page</param>
    /// <param name="result"></param>
    /// <returns></returns>
    public IEnumerable<dynamic> RetrieveCurrentPaginatedData(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string sortingColumnName, int page, int itemsPerPage, FilterResult result)
    {
        string cmd = $"SELECT MIN({sortingColumnName}) FROM {tableName} WHERE {result.WhereClause ?? "1=1"} ORDER BY {sortingColumnName} DESC LIMIT {itemsPerPage} OFFSET {(page - 1) * itemsPerPage}";
        var data = connection.ExecuteScalar<long>(cmd, result.Parameters);

        if (data > 0)
        {
            var list = connection.Query($"SELECT * FROM {tableName} WHERE {result.WhereClause ?? "1=1"} AND {sortingColumnName} >= {data} ORDER BY {sortingColumnName} DESC LIMIT {itemsPerPage} OFFSET {(page - 1) * itemsPerPage};", result.Parameters);
            return list;
        }

        return new List<dynamic>();
    }

    /// <summary>
    /// Adds the name of a column.
    /// </summary>
    /// <param name="sb">The string builder  to append to.</param>
    /// <param name="columnName">The column name.</param>
    public void AppendColumnName(StringBuilder sb, string columnName)
    {
        sb.AppendFormat("\"{0}\"", columnName);
    }

    /// <summary>
    /// Adds a column equality to a parameter.
    /// </summary>
    /// <param name="sb">The string builder  to append to.</param>
    /// <param name="columnName">The column name.</param>
    public void AppendColumnNameEqualsValue(StringBuilder sb, PropertyInfo property)
    {
        sb.AppendFormat("\"{0}\" = @{1}", property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name, property.Name);
    }
}

/// <summary>
/// The SQLite database adapter.
/// </summary>
public partial class SQLiteAdapter : ISqlAdapter
{
    /// <summary>
    /// Inserts <paramref name="entityToInsert"/> into the database, returning the Id of the row created.
    /// </summary>
    /// <param name="connection">The connection to use.</param>
    /// <param name="transaction">The transaction to use.</param>
    /// <param name="commandTimeout">The command timeout to use.</param>
    /// <param name="tableName">The table to insert into.</param>
    /// <param name="columnList">The columns to set with this insert.</param>
    /// <param name="parameterList">The parameters to set for this insert.</param>
    /// <param name="keyProperties">The key columns in this table.</param>
    /// <param name="entityToInsert">The entity to insert.</param>
    /// <returns>The Id of the row created.</returns>
    public int Insert(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var cmd = $"INSERT INTO {tableName} ({columnList}) VALUES ({parameterList}); SELECT last_insert_rowid() id";
        var multi = connection.QueryMultiple(cmd, entityToInsert, transaction, commandTimeout);

        if (keyProperties.Any())
        {
            var id = (int)multi.Read().First().id;
            var propertyInfos = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
            if (propertyInfos.Length == 0) return id;

            var idProperty = propertyInfos[0];
            idProperty.SetValue(entityToInsert, Convert.ChangeType(id, idProperty.PropertyType), null);

            return id;
        }

        var result = multi.Read().Count();

        return result;
    }

    /// <summary>
    /// Retrieve current paginated data
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="connection"></param>
    /// <param name="transaction"></param>
    /// <param name="commandTimeout"></param>
    /// <param name="tableName">The table to insert into.</param>
    /// <param name="sortingColumnName">Sorting column name, such as timestamp or auto-increment column</param>
    /// <param name="page">Current page index</param>
    /// <param name="itemsPerPage">Items for per page</param>
    /// <param name="result"></param>
    /// <returns></returns>
    public IEnumerable<dynamic> RetrieveCurrentPaginatedData(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string sortingColumnName, int page, int itemsPerPage, FilterResult result)
    {
        string cmd = $"SELECT MIN({sortingColumnName}) FROM {tableName} WHERE {result.WhereClause ?? "1=1"} ORDER BY {sortingColumnName} DESC LIMIT {itemsPerPage} OFFSET {(page - 1) * itemsPerPage}";
        var data = connection.ExecuteScalar<long>(cmd, result.Parameters);

        if (data > 0)
        {
            var list = connection.Query($"SELECT * FROM {tableName} WHERE {result.WhereClause ?? "1=1"} AND {sortingColumnName} >= {data} ORDER BY {sortingColumnName} DESC LIMIT {itemsPerPage} OFFSET {(page - 1) * itemsPerPage};", result.Parameters);
            return list;
        }

        return new List<dynamic>();
    }

    /// <summary>
    /// Adds the name of a column.
    /// </summary>
    /// <param name="sb">The string builder  to append to.</param>
    /// <param name="columnName">The column name.</param>
    public void AppendColumnName(StringBuilder sb, string columnName)
    {
        sb.AppendFormat("\"{0}\"", columnName);
    }

    /// <summary>
    /// Adds a column equality to a parameter.
    /// </summary>
    /// <param name="sb">The string builder  to append to.</param>
    /// <param name="columnName">The column name.</param>
    public void AppendColumnNameEqualsValue(StringBuilder sb, PropertyInfo property)
    {
        sb.AppendFormat("\"{0}\" = @{1}", property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name, property.Name);
    }
}

/// <summary>
/// The Firebase SQL adapter.
/// </summary>
public partial class FbAdapter : ISqlAdapter
{
    /// <summary>
    /// Inserts <paramref name="entityToInsert"/> into the database, returning the Id of the row created.
    /// </summary>
    /// <param name="connection">The connection to use.</param>
    /// <param name="transaction">The transaction to use.</param>
    /// <param name="commandTimeout">The command timeout to use.</param>
    /// <param name="tableName">The table to insert into.</param>
    /// <param name="columnList">The columns to set with this insert.</param>
    /// <param name="parameterList">The parameters to set for this insert.</param>
    /// <param name="keyProperties">The key columns in this table.</param>
    /// <param name="entityToInsert">The entity to insert.</param>
    /// <returns>The Id of the row created.</returns>
    public int Insert(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var cmd = $"INSERT INTO {tableName} ({columnList}) VALUES ({parameterList})";
        var result = connection.Execute(cmd, entityToInsert, transaction, commandTimeout);

        if (keyProperties.Any())
        {
            var propertyInfos = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
            var keyName = propertyInfos[0].Name;
            var r = connection.Query($"SELECT FIRST 1 {keyName} ID FROM {tableName} ORDER BY {keyName} DESC", transaction: transaction, commandTimeout: commandTimeout);

            var id = r.First().ID;
            if (id == null) return 0;
            if (propertyInfos.Length == 0) return Convert.ToInt32(id);

            var idp = propertyInfos[0];
            idp.SetValue(entityToInsert, Convert.ChangeType(id, idp.PropertyType), null);

            return Convert.ToInt32(id);
        }

        return result;
    }

    /// <summary>
    /// Retrieve current paginated data
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="connection"></param>
    /// <param name="transaction"></param>
    /// <param name="commandTimeout"></param>
    /// <param name="tableName">The table to insert into.</param>
    /// <param name="sortingColumnName">Sorting column name, such as timestamp or auto-increment column</param>
    /// <param name="page">Current page index</param>
    /// <param name="itemsPerPage">Items for per page</param>
    /// <param name="result"></param>
    /// <returns></returns>
    public IEnumerable<dynamic> RetrieveCurrentPaginatedData(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string sortingColumnName, int page, int itemsPerPage, FilterResult result)
    {
        string cmd = $"SELECT MIN({sortingColumnName}) FROM {tableName} WHERE {result.WhereClause ?? "1=1"} ORDER BY {sortingColumnName} DESC ROWS {(page - 1) * itemsPerPage + 1} TO {(page * itemsPerPage)}";
        var data = connection.Query(cmd, result.Parameters);

        if (data != null && data.Any())
        {
            var list = connection.Query($"SELECT * FROM {tableName} WHERE {result.WhereClause ?? "1=1"} AND {sortingColumnName} >= {data.First()} ORDER BY {sortingColumnName} DESC ROWS {(page - 1) * itemsPerPage} TO {page * itemsPerPage};", result.Parameters);
            return list;
        }

        return new List<dynamic>();
    }

    /// <summary>
    /// Adds the name of a column.
    /// </summary>
    /// <param name="sb">The string builder  to append to.</param>
    /// <param name="columnName">The column name.</param>
    public void AppendColumnName(StringBuilder sb, string columnName)
    {
        sb.AppendFormat("{0}", columnName);
    }

    /// <summary>
    /// Adds a column equality to a parameter.
    /// </summary>
    /// <param name="sb">The string builder  to append to.</param>
    /// <param name="columnName">The column name.</param>
    public void AppendColumnNameEqualsValue(StringBuilder sb, PropertyInfo property)
    {
        sb.AppendFormat("{0} = @{1}", property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name, property.Name);
    }
}

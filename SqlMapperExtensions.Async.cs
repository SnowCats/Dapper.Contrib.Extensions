﻿using System.Data;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Dapper;
using Dawning.Auth.Dapper.Contrib;

namespace Dawning.Auth.Dapper.Contrib
{
    public static partial class SqlMapperExtensions
    {
        /// <summary>
        /// Returns a single entity by a single id from table "Ts" asynchronously using Task. T must be of interface type. 
        /// Id must be marked with [Key] attribute.
        /// Created entity is tracked/intercepted for changes and used by the Update() extension. 
        /// </summary>
        /// <typeparam name="T">Interface type to create and populate</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="id">Id of the entity to get, must be marked with [Key] attribute</param>
        /// <param name="transaction">The transaction to run under, null (the default) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <returns>Entity of T</returns>
        public static async Task<T> GetAsync<T>(this IDbConnection connection, dynamic id, IDbTransaction transaction = null, int? commandTimeout = null) where T : class, new()
        {
            var type = typeof(T);
            if (!GetQueries.TryGetValue(type.TypeHandle, out string sql))
            {
                var property = GetSingleKey<T>(nameof(GetAsync));
                var key = property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name;
                var name = GetTableName(type);

                sql = $"SELECT * FROM {name} WHERE {key} = @id";
                GetQueries[type.TypeHandle] = sql;
            }

            var dynParams = new DynamicParameters();
            dynParams.Add("@id", id);

            var obj = (await connection.QueryAsync(sql, dynParams, transaction, commandTimeout: commandTimeout)).FirstOrDefault();
            return GetImpl<T>(obj, type);
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
        public static async Task<IEnumerable<T>> GetPagedListAsync<T, TModel>(this IDbConnection connection, Expression<Func<T, bool>> filter, TModel model, int page, int itemsPerPage, IDbTransaction transaction = null, int? commandTimeout = null, ISqlAdapter sqlAdapter = null) where T : class, new() where TModel : class, new()
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
                GetSingleKey<T>(nameof(GetPagedListAsync));
                sql = $"SELECT * FROM {name} WHERE {result.WhereClause ?? "1=1"}";
                GetQueries[cacheType.TypeHandle] = sql;
            }

            // 获取分页记录
            var list = await sqlAdapter.RetrieveCurrentPaginatedDataAsync(connection, transaction, commandTimeout, name, defaultSortName, page, itemsPerPage, result);

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
        public static async Task<int> GetCountAsync<T, TModel>(this IDbConnection connection, Expression<Func<T, bool>> filter, TModel model, string defaultSortingColumnName, int page, int itemsPerPage, IDbTransaction transaction = null, int? commandTimeout = null, ISqlAdapter sqlAdapter = null) where T : class, new() where TModel : class, new()
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
                GetSingleKey<T>(nameof(GetPagedListAsync));

                sql = $"SELECT COUNT(*) FROM {name} WHERE {result.WhereClause ?? "1=1"}";
                GetQueries[cacheType.TypeHandle] = sql;
            }

            var count = await connection.ExecuteScalarAsync(sql, result.Parameters, transaction, commandTimeout);
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
        public static async Task<IEnumerable<T>> GetListAsync<T, TModel>(this IDbConnection connection, Expression<Func<T, bool>> filter, TModel model, IDbTransaction transaction = null, int? commandTimeout = null)
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

            var list = await connection.QueryAsync(sql, param: result.Parameters, transaction: transaction, commandTimeout: commandTimeout).ConfigureAwait(false);

            return GetListImpl<T>(list, type);
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
        public static async Task<IEnumerable<T>> GetAllAsync<T>(this IDbConnection connection, IDbTransaction transaction = null, int? commandTimeout = null) where T : class, new()
        {
            var type = typeof(T);
            var cacheType = typeof(List<T>);

            if (!GetQueries.TryGetValue(cacheType.TypeHandle, out string sql))
            {
                GetSingleKey<T>(nameof(GetAll));
                var name = GetTableName(type);

                sql = "SELECT * FROM " + name;
                GetQueries[cacheType.TypeHandle] = sql;
            }

            var result = await connection.QueryAsync(sql, transaction: transaction, commandTimeout: commandTimeout).ConfigureAwait(false);
            // return GetAllAsyncImpl<T>(result, type);
            return GetListImpl<T>(result, type);
        }

        private static IEnumerable<T> GetAllAsyncImpl<T>(dynamic result, Type type) where T : class, new()
        {
            
            var list = new List<T>();
            foreach (IDictionary<string, object> res in result)
            {
                var obj = ProxyGenerator.GetInterfaceProxy<T>();
                foreach (var property in TypePropertiesCache(type))
                {
                    var val = res[property.Name];
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
                ((IProxy)obj).IsDirty = false;   //reset change tracking and return
                list.Add(obj);
            }
            return list;
        }

        /// <summary>
        /// Inserts an entity into table "Ts" asynchronously using Task and returns identity id.
        /// </summary>
        /// <typeparam name="T">The type being inserted.</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="entityToInsert">Entity to insert</param>
        /// <param name="transaction">The transaction to run under, null (the default) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <param name="sqlAdapter">The specific ISqlAdapter to use, auto-detected based on connection if null</param>
        /// <returns>Identity of inserted entity</returns>
        public static Task<int> InsertAsync<T>(this IDbConnection connection, T entityToInsert, IDbTransaction transaction = null,
            int? commandTimeout = null, ISqlAdapter sqlAdapter = null) where T : class
        {
            var type = typeof(T);
            sqlAdapter ??= GetFormatter(connection);

            var isList = false;
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
            var keyProperties = KeyPropertiesCache(type).ToList();
            var computedProperties = ComputedPropertiesCache(type);
            var allPropertiesExceptKeyAndComputed = allProperties.Except(keyProperties.Union(computedProperties)).ToList();

            for (var i = 0; i < allPropertiesExceptKeyAndComputed.Count; i++)
            {
                var property = allPropertiesExceptKeyAndComputed[i];
                string columnName = property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name;
                sqlAdapter.AppendColumnName(sbColumnList, columnName);
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

            if (!isList)    //single entity
            {
                return sqlAdapter.InsertAsync(connection, transaction, commandTimeout, name, sbColumnList.ToString(),
                    sbParameterList.ToString(), keyProperties, entityToInsert);
            }

            //insert list of entities
            var cmd = $"INSERT INTO {name} ({sbColumnList}) values ({sbParameterList})";
            return connection.ExecuteAsync(cmd, entityToInsert, transaction, commandTimeout);
        }

        /// <summary>
        /// Updates entity in table "Ts" asynchronously using Task, checks if the entity is modified if the entity is tracked by the Get() extension.
        /// </summary>
        /// <typeparam name="T">Type to be updated</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="entityToUpdate">Entity to be updated</param>
        /// <param name="transaction">The transaction to run under, null (the default) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <returns>true if updated, false if not found or not modified (tracked entities)</returns>
        public static async Task<bool> UpdateAsync<T>(this IDbConnection connection, T entityToUpdate, IDbTransaction transaction = null, int? commandTimeout = null) where T : class
        {
            if ((entityToUpdate is IProxy proxy) && !proxy.IsDirty)
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

            var keyProperties = KeyPropertiesCache(type).ToList();
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
                adapter.AppendColumnNameEqualsValue(sb, property);
                if (i < nonIdProps.Count - 1)
                    sb.Append(", ");
            }
            sb.Append(" where ");
            for (var i = 0; i < keyProperties.Count; i++)
            {
                var property = keyProperties[i];
                adapter.AppendColumnNameEqualsValue(sb, property);
                if (i < keyProperties.Count - 1)
                    sb.Append(" and ");
            }
            var updated = await connection.ExecuteAsync(sb.ToString(), entityToUpdate, commandTimeout: commandTimeout, transaction: transaction).ConfigureAwait(false);
            return updated > 0;
        }

        /// <summary>
        /// Delete entity in table "Ts" asynchronously using Task.
        /// </summary>
        /// <typeparam name="T">Type of entity</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="entityToDelete">Entity to delete</param>
        /// <param name="transaction">The transaction to run under, null (the default) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <returns>true if deleted, false if not found</returns>
        public static async Task<bool> DeleteAsync<T>(this IDbConnection connection, T entityToDelete, IDbTransaction transaction = null, int? commandTimeout = null) where T : class
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

            var keyProperties = KeyPropertiesCache(type);
            var explicitKeyProperties = ExplicitKeyPropertiesCache(type);
            if (keyProperties.Count == 0 && explicitKeyProperties.Count == 0)
                throw new ArgumentException("Entity must have at least one [Key] or [ExplicitKey] property");

            var name = GetTableName(type);
            var allKeyProperties = keyProperties.Concat(explicitKeyProperties).ToList();

            var sb = new StringBuilder();
            sb.AppendFormat("DELETE FROM {0} WHERE ", name);

            var adapter = GetFormatter(connection);

            for (var i = 0; i < allKeyProperties.Count; i++)
            {
                var property = allKeyProperties[i];
                adapter.AppendColumnNameEqualsValue(sb, property);
                if (i < allKeyProperties.Count - 1)
                    sb.Append(" AND ");
            }
            var deleted = await connection.ExecuteAsync(sb.ToString(), entityToDelete, transaction, commandTimeout).ConfigureAwait(false);
            return deleted > 0;
        }

        /// <summary>
        /// Delete all entities in the table related to the type T asynchronously using Task.
        /// </summary>
        /// <typeparam name="T">Type of entity</typeparam>
        /// <param name="connection">Open SqlConnection</param>
        /// <param name="transaction">The transaction to run under, null (the default) if none</param>
        /// <param name="commandTimeout">Number of seconds before command execution timeout</param>
        /// <returns>true if deleted, false if none found</returns>
        public static async Task<bool> DeleteAllAsync<T>(this IDbConnection connection, IDbTransaction transaction = null, int? commandTimeout = null) where T : class
        {
            var type = typeof(T);
            var statement = "DELETE FROM " + GetTableName(type);
            var deleted = await connection.ExecuteAsync(statement, null, transaction, commandTimeout).ConfigureAwait(false);
            return deleted > 0;
        }
    }
}

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
    Task<int> InsertAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert);

    /// <summary>
    /// Retrieve the current paginated data based on the sorted column names.
    /// </summary>
    /// <returns></returns>
    Task<IEnumerable<dynamic>> RetrieveCurrentPaginatedDataAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string sortingColumnName, int page, int itemsPerPage, FilterResult result);
}

public partial class SqlServerAdapter
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
    public async Task<int> InsertAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var cmd = $"INSERT INTO {tableName} ({columnList}) values ({parameterList}); SELECT SCOPE_IDENTITY() id";
        var multi = await connection.QueryMultipleAsync(cmd, entityToInsert, transaction, commandTimeout).ConfigureAwait(false);

        if (keyProperties.Any())
        {
            var first = await multi.ReadFirstOrDefaultAsync().ConfigureAwait(false);
            if (first == null || first.id == null) return 0;

            var id = (int)first.id;
            var pi = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
            if (pi.Length == 0) return id;

            var idp = pi[0];
            idp.SetValue(entityToInsert, Convert.ChangeType(id, idp.PropertyType), null);

            return id;
        }

        var result = (await multi.ReadAsync().ConfigureAwait(false)).Count();

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
    public async Task<IEnumerable<dynamic>> RetrieveCurrentPaginatedDataAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string sortingColumnName, int page, int itemsPerPage, FilterResult result)
    {
        string cmd = $"SELECT MIN({sortingColumnName}) FROM (SELECT {sortingColumnName} FROM {tableName} WHERE {result.WhereClause ?? "1=1"} ORDER BY {sortingColumnName} DESC OFFSET {(page - 1) * itemsPerPage} ROWS FETCH NEXT {itemsPerPage} ROWS ONLY) AS t";
        var data = await connection.ExecuteScalarAsync<long>(cmd, result.Parameters);

        if (data > 0)
        {
            var list = await connection.QueryAsync($"SELECT * FROM {tableName} WHERE {result.WhereClause ?? "1=1"} AND {sortingColumnName} >= {data} ORDER BY {sortingColumnName} DESC OFFSET {(page - 1) * itemsPerPage} ROWS FETCH NEXT {itemsPerPage} ROWS ONLY;", result.Parameters);
            return list;
        }

        return new List<dynamic>();
    }
}

public partial class SqlCeServerAdapter
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
    public async Task<int> InsertAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var cmd = $"INSERT INTO {tableName} ({columnList}) VALUES ({parameterList})";
        var result = await connection.ExecuteAsync(cmd, entityToInsert, transaction, commandTimeout).ConfigureAwait(false);

        if (keyProperties.Any() && result > 0)
        {
            var r = (await connection.QueryAsync<dynamic>("SELECT @@IDENTITY id", transaction: transaction, commandTimeout: commandTimeout).ConfigureAwait(false)).ToList();

            if (r[0] == null || r[0].id == null) return 0;
            var id = (int)r[0].id;

            var pi = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
            if (pi.Length == 0) return id;

            var idp = pi[0];
            idp.SetValue(entityToInsert, Convert.ChangeType(id, idp.PropertyType), null);

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
    public async Task<IEnumerable<dynamic>> RetrieveCurrentPaginatedDataAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string sortingColumnName, int page, int itemsPerPage, FilterResult result)
    {
        string cmd = $"SELECT MIN({sortingColumnName} FROM (SELECT {sortingColumnName}, ROW_NUMBER() OVER (ORDER BY {sortingColumnName} DESC) AS row_num FROM {tableName} WHERE {result.WhereClause ?? "1=1"}) AS t WHERE row_num BETWEEN {(page - 1) * itemsPerPage + 1} AND {page * itemsPerPage}";
        var data = await connection.ExecuteScalarAsync<long>(cmd, result.Parameters);

        if (data > 0)
        {
            var list = await connection.QueryAsync($"SELECT * FROM {tableName} WHERE {result.WhereClause ?? "1=1"} AND {sortingColumnName} >= {data} ORDER BY {sortingColumnName} DESC;", result.Parameters);
            return list;
        }

        return new List<dynamic>();
    }
}

public partial class MySqlAdapter
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
    public async Task<int> InsertAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName,
        string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var cmd = $"INSERT INTO {tableName} ({columnList}) VALUES ({parameterList})";
        var result = await connection.ExecuteAsync(cmd, entityToInsert, transaction, commandTimeout).ConfigureAwait(false);

        if (keyProperties.Any() && result > 0)
        {
            var r = await connection.QueryAsync<dynamic>("SELECT LAST_INSERT_ID() id", transaction: transaction, commandTimeout: commandTimeout).ConfigureAwait(false);

            var id = r.First().id;
            if (id == null) return 0;
            var pi = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
            if (pi.Length == 0) return Convert.ToInt32(id);

            var idp = pi[0];
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
    public async Task<IEnumerable<dynamic>> RetrieveCurrentPaginatedDataAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string sortingColumnName, int page, int itemsPerPage, FilterResult result)
    {
        string cmd = $"SELECT MIN(`{sortingColumnName}`) FROM {tableName} WHERE {result.WhereClause ?? "1=1"} ORDER BY `{sortingColumnName}` DESC LIMIT {(page - 1) * itemsPerPage}, {itemsPerPage}";
        var data = await connection.ExecuteScalarAsync<long>(cmd, result.Parameters);

        if (data > 0)
        {
            var list = await connection.QueryAsync($"SELECT * FROM {tableName} WHERE {result.WhereClause ?? "1=1"} AND {sortingColumnName} >= {data} ORDER BY {sortingColumnName} DESC LIMIT {(page - 1) * itemsPerPage}, {itemsPerPage}", result.Parameters);
            return list;
        }

        return new List<dynamic>();
    }
}

public partial class PostgresAdapter
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
    public async Task<int> InsertAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var sb = new StringBuilder();
        sb.AppendFormat("INSERT INTO {0} ({1}) VALUES ({2})", tableName, columnList, parameterList);

        // If no primary key then safe to assume a join table with not too much data to return
        var propertyInfos = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
        if (propertyInfos.Length == 0)
        {
            sb.Append(" RETURNING *");
        }
        else
        {
            sb.Append(" RETURNING ");
            bool first = true;
            foreach (var property in propertyInfos)
            {
                if (!first)
                    sb.Append(", ");
                first = false;
                sb.Append(property.GetCustomAttribute<ColumnAttribute>()?.Name ?? property.Name);
            }
        }

        var results = await connection.QueryAsync(sb.ToString(), entityToInsert, transaction, commandTimeout).ConfigureAwait(false);

        if (keyProperties.Any())
        {
            // Return the key by assigning the corresponding property in the object - by product is that it supports compound primary keys
            var id = 0;
            foreach (var p in propertyInfos)
            {
                var value = ((IDictionary<string, object>)results.First())[p.Name.ToLower()];
                p.SetValue(entityToInsert, value, null);
                if (id == 0)
                    id = Convert.ToInt32(value);
            }
            return id;
        }

        return results.Count();
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
    public async Task<IEnumerable<dynamic>> RetrieveCurrentPaginatedDataAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string sortingColumnName, int page, int itemsPerPage, FilterResult result)
    {
        string cmd = $"SELECT MIN({sortingColumnName}) FROM {tableName} WHERE {result.WhereClause ?? "1=1"} ORDER BY {sortingColumnName} DESC LIMIT {itemsPerPage} OFFSET {(page - 1) * itemsPerPage}";
        var data = await connection.ExecuteScalarAsync<long>(cmd, result.Parameters);

        if (data > 0)
        {
            var list = await connection.QueryAsync($"SELECT * FROM {tableName} WHERE {result.WhereClause ?? "1=1"} AND {sortingColumnName} >= {data} ORDER BY {sortingColumnName} DESC LIMIT {itemsPerPage} OFFSET {(page - 1) * itemsPerPage};", result.Parameters);
            return list;
        }

        return new List<dynamic>();
    }
}

public partial class SQLiteAdapter
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
    public async Task<int> InsertAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var cmd = $"INSERT INTO {tableName} ({columnList}) VALUES ({parameterList}); SELECT last_insert_rowid() id";
        var multi = await connection.QueryMultipleAsync(cmd, entityToInsert, transaction, commandTimeout).ConfigureAwait(false);

        if (keyProperties.Any())
        {
            var id = (int)(await multi.ReadFirstAsync().ConfigureAwait(false)).id;
            var pi = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
            if (pi.Length == 0) return id;

            var idp = pi[0];
            idp.SetValue(entityToInsert, Convert.ChangeType(id, idp.PropertyType), null);

            return id;
        }

        var result = (await multi.ReadAsync().ConfigureAwait(false)).Count();

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
    public async Task<IEnumerable<dynamic>> RetrieveCurrentPaginatedDataAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string sortingColumnName, int page, int itemsPerPage, FilterResult result)
    {
        string cmd = $"SELECT MIN({sortingColumnName}) FROM {tableName} ORDER BY {sortingColumnName} DESC LIMIT {itemsPerPage} OFFSET {(page - 1) * itemsPerPage}";
        var data = await connection.ExecuteScalarAsync<long>(cmd, result.Parameters);

        if (data > 0)
        {
            var list = await connection.QueryAsync($"SELECT * FROM {tableName} WHERE {result.WhereClause} AND {sortingColumnName} >= {data} ORDER BY {sortingColumnName} DESC LIMIT {itemsPerPage} OFFSET {(page - 1) * itemsPerPage};", result.Parameters);
            return list;
        }

        return new List<dynamic>();
    }
}

public partial class FbAdapter
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
    public async Task<int> InsertAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string columnList, string parameterList, IEnumerable<PropertyInfo> keyProperties, object entityToInsert)
    {
        var cmd = $"INSERT INTO {tableName} ({columnList}) VALUES ({parameterList})";
        var result = await connection.ExecuteAsync(cmd, entityToInsert, transaction, commandTimeout).ConfigureAwait(false);

        if (keyProperties.Any())
        {
            var propertyInfos = keyProperties as PropertyInfo[] ?? keyProperties.ToArray();
            var keyName = propertyInfos[0].Name;
            var r = await connection.QueryAsync($"SELECT FIRST 1 {keyName} ID FROM {tableName} ORDER BY {keyName} DESC", transaction: transaction, commandTimeout: commandTimeout).ConfigureAwait(false);

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
    public async Task<IEnumerable<dynamic>> RetrieveCurrentPaginatedDataAsync(IDbConnection connection, IDbTransaction transaction, int? commandTimeout, string tableName, string sortingColumnName, int page, int itemsPerPage, FilterResult result)
    {
        string cmd = $"SELECT MIN({sortingColumnName}) FROM {tableName} WHERE {result.WhereClause ?? "1=1"} ORDER BY {sortingColumnName} DESC ROWS {(page - 1) * itemsPerPage + 1} TO {(page * itemsPerPage)}";
        var data = await connection.ExecuteScalarAsync<long>(cmd, result.Parameters);

        if (data > 0)
        {
            var list = await connection.QueryAsync($"SELECT * FROM {tableName} WHERE {result.WhereClause ?? "1=1"} AND {sortingColumnName} >= {data} ORDER BY {sortingColumnName} DESC ROWS {(page - 1) * itemsPerPage} TO {page * itemsPerPage};", result.Parameters);
            return list;
        }

        return new List<dynamic>();
    }
}
using GraphQL.Types;

using System.Text;
using System.Text.RegularExpressions;

var schema = Schema.For(File.ReadAllText(@"D:\sv-kubernetes\applications\sv-test\containers\graphql-v1\schema.graphql"), builder => {
	builder.IgnoreComments = false;
	builder.AllowUnknownTypes = true;
	builder.AllowUnknownFields = true;
});

var sb = new StringBuilder();

var queryType = schema.Query.Fields.FirstOrDefault();
var mutationType = schema.Mutation.Fields.FirstOrDefault();

var ignoreTypes = new HashSet<string>(["Query", "Mutation",
	(queryType.ResolvedType as GraphQLTypeReference).TypeName,
	(mutationType.ResolvedType as GraphQLTypeReference).TypeName
]);

var types = schema.AdditionalTypeInstances.Where(t => !ignoreTypes.Contains(t.Name));
var typeNames = types.Select(t => t.Name).ToHashSet();

if (queryType is not null) {
	AppendFieldType(sb, schema, queryType, "query");
}

if (mutationType != null) {
	AppendFieldType(sb, schema, mutationType, "mutation");
}

var prefixNameSnakeCase = GetCommonStringPrefix(queryType.Name, mutationType.Name).TrimEnd('_');
var prefixNamePascalCase = Regex.Replace(prefixNameSnakeCase, @"(?:^|_)([a-zA-Z])", match => match.Groups[1].Value.ToUpper());

var typesSb = new StringBuilder();

foreach (var type in types) {
	if (type is EnumerationGraphType en) {
		typesSb.AppendLine($@"
export type {type.Name} = {string.Join(" | ", en.Values.Select(v => $"'{v.Name}'"))};");

		continue;
	}

	if (type is ComplexGraphType<object> ct) {
		var fieldsSb = new StringBuilder();

		foreach (var field in ct.Fields) {
			var fieldArg = GetFieldType(field);
			var tsTypeName = GetTypescriptTypeFromGraphType(fieldArg.TypeName);

			if (fieldArg.IsList) {
				tsTypeName = $"{tsTypeName}[]";
			}

			if (!string.IsNullOrWhiteSpace(field.Description)) {
				fieldsSb.AppendLine($@"	/**
	{field.Description.Trim()}
	*/");
			}

			fieldsSb.AppendLine($"	{field.Name}{(!fieldArg.IsMandatory ? "?" : "")}: {tsTypeName};");
		}

		typesSb.AppendLine($@"
export type {type.Name} = {{
{fieldsSb}}};");

		continue;
	}

	throw new InvalidOperationException($"Unsupported field name {type.Name}");
}

Console.WriteLine(@$"
import {{ query }} from ""@simpleview/sv-graphql-client"";
import {{ GraphContext, GraphServer }} from ""@simpleview/sv-graphql-client"";

export type Ctor = {{
	graphUrl: string,
	graphServer: GraphServer
}};

export type Call<Input> = {{
	input: Input,
	fields: string,
	context?: GraphContext,
	headers?: Record<string, any>
}};

{typesSb}

class {prefixNamePascalCase}Prefix {{
	name: string;
	
	#graphUrl: string;
	#graphServer: GraphServer;

	constructor({{ graphUrl, graphServer }}: Ctor) {{
		this.name = ""{prefixNameSnakeCase}"";

		this.#graphUrl = graphUrl;
		this.#graphServer = graphServer;
	}}

{sb}
}}

export default {prefixNamePascalCase}Prefix;
");

void AppendFieldType(StringBuilder sb, Schema schema, FieldType fieldType, string prefix) {
	var queryResolveTypeName = (fieldType?.ResolvedType as GraphQLTypeReference)?.TypeName;
	var queryResolveType = schema.AdditionalTypeInstances.FirstOrDefault(t => t.Name == queryResolveTypeName) as ObjectGraphType;

	foreach (var field in queryResolveType.Fields) {
		var querySb = new IndentedStringBuilder();
		querySb.Indent();
		querySb.Indent();
		querySb.Indent();
		querySb.Indent();

		querySb.AppendLine($"{prefix} {GetQueryArguments(fieldType.Arguments.Concat(field.Arguments))} {{");

		querySb.Indent();
		querySb.AppendLine($"{queryResolveType.Name} {GetQueryArguments(fieldType.Arguments, mapping: true)} {{");

		querySb.Indent();

		querySb.AppendLine($"{field.Name} {GetQueryArguments(field.Arguments, mapping: true)} {{");

		querySb.Indent();
		querySb.AppendLine("${fields}");

		querySb.Unindent();
		querySb.AppendLine("}");

		querySb.Unindent();
		querySb.AppendLine("}");

		querySb.Unindent();
		querySb.AppendLine("}");

		if (!string.IsNullOrWhiteSpace(field.Description)) {
			sb.Append($@"
	/**
	{field.Description.Trim()}
	*/");
		}

		Console.WriteLine();

		var resolvedType = GetFieldType(field.ResolvedType);
		var typeName = GetTypescriptTypeFromGraphType(resolvedType.TypeName);

		if (resolvedType.IsList) {
			typeName = $"{typeName}[]";
		}

		// TODO: Would be nice to denote when a resolved value is optional, but few fields specify that they're required.
		//if (!resolvedType.IsMandatory) {
		//	typeName = $"{typeName} | undefined";
		//}

		sb.Append($@"
	async {field.Name}({{ input, fields, context, headers }}: Call<any> /* TODO */): Promise<{typeName}> {{
		context = context || this.#graphServer.context;

		const variables = {{
			input,
			acct_id: context.acct_id
		}}

		return await query<{typeName}>({{
			query: `
{querySb}			`,
			variables,
			url: this.#graphUrl,
			token: context.token,
			headers,
			clean: true,
			key: ""{queryResolveType.Name}.{field.Name}""
		}});
	}}
		");
	}
}

string GetQueryArguments(IEnumerable<QueryArgument> args, bool mapping = false) {
	var fieldSb = new StringBuilder();

	var argArray = args.ToArray();

	if (argArray.Length != 0) {
		fieldSb.Append('(');

		var argIndex = 0;

		foreach (var arg in ParseQueryArguments(argArray)) {
			fieldSb.Append($"{(!mapping ? "$" : "")}{arg.Name}");
			fieldSb.Append(": ");
			fieldSb.Append(!mapping ? arg.TypeName : $"${arg.Name}");

			if (arg.IsMandatory) {
				fieldSb.Append('!');
			}

			if (argIndex++ < argArray.Length - 1) {
				fieldSb.Append(", ");
			}
		}

		fieldSb.Append(')');
	}

	return fieldSb.ToString();
}

IEnumerable<FieldArgument> ParseQueryArguments(QueryArgument[] args) {
	foreach (var arg in args) {
		var field = GetFieldType(arg);

		field.Name = arg.Name;

		yield return field;
	}
}

string GetCommonStringPrefix(string first, string second) {
	var chars = new List<char>();

	for (int i = 0; i < Math.Min(first.Length, second.Length); i++) {
		if (first[i] != second[i]) {
			break;
		}

		chars.Add(first[i]);
	}

	return new string(chars.ToArray());
}

FieldArgument GetFieldType(object field, bool mandatory = false, bool isList = false) {
	if (field is GraphQLTypeReference rt) {
		return new FieldArgument(rt.TypeName, mandatory, isList);
	}
	if (field is EnumerationGraphType et) {
		return new FieldArgument(et.Name, mandatory, isList);
	}
	if (field is ObjectGraphType ogt) {
		return new FieldArgument(ogt.Name, mandatory, isList);
	}
	if (field is FieldType ft) {
		return GetFieldType(ft.ResolvedType, mandatory, isList);
	}
	if (field is QueryArgument qa) {
		return GetFieldType(qa.ResolvedType, mandatory, isList);
	}
	if (field is NonNullGraphType nnt) {
		return GetFieldType(nnt.ResolvedType, true, isList);
	}
	if (field is ListGraphType lt) {
		return GetFieldType(lt.ResolvedType, mandatory, true);
	}

	// TODO: Name
	throw new Exception($"Unsupported field type");
}

string GetTypescriptTypeFromGraphType(string typeName) {
	return typeName switch {
		"String" => "string",
		"Int" => "number",
		"Boolean" => "boolean",
		"EmailAddress" => "string",
		"Date" => "string",
		"JSONObject" => "any",
		var x when typeNames.Contains(x) => x,
		_ => "any"
	};
}

record FieldArgument(string TypeName, bool IsMandatory, bool IsList) {
	public string Name { get; set; }
}

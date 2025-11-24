using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Reflection.Emit;

internal class CommandLineParsing
{
	public static async Task<int> Interpret(string[] args, IServiceProvider serviceProvider)
	{
		var logger = serviceProvider.GetRequiredService<ILogger<CommandLineParsing>>();
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		var arguments = args;
		var verbs = DetermineVerbs();
		// (var arguments, var verbs) = DetermineVerbGroup(args);
		var parserResult = Parser.Default.ParseArguments(arguments, verbs);
		stopwatch.Stop();
		logger.LogDebug("Parsed command line in {elapsed}ms", stopwatch.ElapsedMilliseconds);

		var parsed = parserResult as Parsed<object>;

		if (parsed != null)
		{
			var action = parsed.Value;
			logger.LogDebug("Invoking action {action}", action.GetType().FullName);
			var actionInvocationMethod = action.GetType().GetMethods().Single(m => !m.IsSpecialName && !m.IsStatic && m.DeclaringType == action.GetType());
			try
			{
				var methodArguments = actionInvocationMethod.GetParameters()
					.Select(p => serviceProvider.GetRequiredKeyedService(p.ParameterType, p.GetCustomAttribute<FromKeyedServicesAttribute>()?.Key))
					.ToArray();

				var result = actionInvocationMethod.Invoke(action, methodArguments);
				if (result is Task t)
				{
					await t;
				}
				return 0;
			}
			catch (TargetInvocationException e)
			{
				logger.LogError(e, "Cannot invoke command.");
				return 1;
			}
			catch (Exception e)
			{
				logger.LogError(e, "Unknown error.");
				return 1;
			}
		}
		else
		{
			return 1;
		}
	}

	static Type[] DetermineVerbs()
	{
		var verbs = typeof(Program).Assembly.GetTypes().Where(t => t.GetCustomAttribute(typeof(VerbAttribute)) != null).OrderBy(t => t.Name).ToArray();
		return verbs;
	}

	static (string[] arguments, Type[] verbs) DetermineVerbGroup(string[] args)
	{
		/* Verbs exist in namespace "...Verbs". If they are in a sub-namespace like "...Verbs.Group1",
		 * they are grouped into a verb group "Group1".
		 * Verb groups can be invoked by starting the application arguments with the goup name followed with one of the verbs of that group.
		 * That way we can have verbs for different use cases and technologies, without users losing high level overview of supported functionality.
		 */
		var actions = typeof(Program).Assembly.GetTypes().Where(t => t.GetCustomAttribute(typeof(VerbAttribute)) != null).OrderBy(t => t.Name).ToList();
		var groupedActions = actions.GroupBy(t => t.Namespace).Select(g => new
		{
			key = g.Key?.Replace("AgenticTodos.Cli.Verbs", string.Empty).Trim('.') ?? string.Empty /*each namespace beneath "Verbs" becomes a group*/,
			actions = g.ToArray()
		}).ToList();

		var groupSelector = args.Select(s => s.Replace("-", "").ToLowerInvariant()).FirstOrDefault();
		var group = groupedActions.SingleOrDefault(g => g.key.Replace(".", "").ToLowerInvariant() == groupSelector);
		if (group != null)
		{
			return (args.Skip(1).ToArray(), group.actions);
		}
		else
		{
			var groupSurrogateActions = CreateRuntimeActions(groupedActions
				.Where(g => !string.IsNullOrEmpty(g.key))
				.Select(g => (name: g.key.Replace(".", "-").ToLowerInvariant(), helptext: $"Invoke {g.key} actions."))
				.ToArray()
			);

			var defaultGroup = groupedActions.Single(g => g.key == string.Empty);
			return (args, defaultGroup.actions.Concat(groupSurrogateActions).ToArray());
		}

		static Type[] CreateRuntimeActions((string name, string helptext)[] actionInfos)
		{
			var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(Guid.NewGuid().ToString()), AssemblyBuilderAccess.Run);
			var moduleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");
			var verbType = typeof(VerbAttribute).GetTypeInfo();

			return actionInfos
				.Select(a =>
				{
					var actionName = a.name;
					var actionHelpText = a.helptext;

					var customAttributeBuilder = new CustomAttributeBuilder(
						con: verbType.GetConstructor([typeof(string), typeof(bool), typeof(string[])])!,
						constructorArgs: [actionName, false, Array.Empty<string>()],
						namedProperties: [verbType.GetProperty("HelpText")!],
						propertyValues: [actionHelpText]
					);

					var action = moduleBuilder.DefineType(Guid.NewGuid().ToString(), TypeAttributes.Public | TypeAttributes.Class);
					action.SetCustomAttribute(customAttributeBuilder);

					return action;
				})
				.Select(t => t.CreateType()!)
				.ToArray();
		}
	}
}

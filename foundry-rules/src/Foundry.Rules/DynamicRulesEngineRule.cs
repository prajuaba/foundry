using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RulesEngine.Models;

namespace Foundry.Rules;

/// <summary>
/// A generic rule implementation that evaluates dynamic, JSON-configured rules loaded from the rule store using Microsoft.RulesEngine.
/// </summary>
/// <typeparam name="TRequest">The type of the incoming command or request.</typeparam>
public class DynamicRulesEngineRule<TRequest> : IBusinessRule<TRequest>
{
    private readonly IDynamicRuleStore _ruleStore;

    private static readonly ReSettings _settings = new ReSettings
    {
        CustomTypes = new[]
        {
            typeof(Math),
            typeof(Convert),
            typeof(string),
            typeof(decimal),
            typeof(DateTime),
            typeof(TimeSpan),
            typeof(Guid)
        }
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicRulesEngineRule{TRequest}"/> class.
    /// </summary>
    /// <param name="ruleStore">The database/in-memory configuration store of dynamic rules.</param>
    public DynamicRulesEngineRule(IDynamicRuleStore ruleStore)
    {
        _ruleStore = ruleStore ?? throw new ArgumentNullException(nameof(ruleStore));
    }

    /// <inheritdoc />
    public async Task<RuleResult> ValidateAsync(TRequest request, CancellationToken ct)
    {
        if (request == null)
        {
            return RuleResult.Success();
        }

        var targetObject = GetTargetObject(request);
        var targetEntityName = targetObject.GetType().Name;

        var dynamicRules = (await _ruleStore.GetRulesForEntityAsync(targetEntityName, ct)).ToList();
        if (!dynamicRules.Any())
        {
            return RuleResult.Success();
        }

        // Map dynamic rules into Microsoft.RulesEngine Workflow model
        var rulesEngineRules = dynamicRules.Select(r => new Rule
        {
            RuleName = r.RuleName,
            ErrorMessage = r.ErrorMessage,
            SuccessEvent = r.ErrorCode,
            Expression = string.IsNullOrWhiteSpace(r.Expression)
                ? BuildExpression(targetObject, r)
                : r.Expression
        }).ToList();

        var workflow = new Workflow
        {
            WorkflowName = targetEntityName,
            Rules = rulesEngineRules
        };

        var rulesEngine = new RulesEngine.RulesEngine(new[] { workflow }, _settings);
        var executionResults = await rulesEngine.ExecuteAllRulesAsync(targetEntityName, targetObject);

        foreach (var result in executionResults)
        {
            if (!result.IsSuccess)
            {
                var failedRule = result.Rule;
                return RuleResult.Failure(failedRule.ErrorMessage ?? "Validation failed.", failedRule.SuccessEvent);
            }
        }

        return RuleResult.Success();
    }

    private object GetTargetObject(TRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var requestType = request.GetType();
        var entityProperty = requestType.GetProperty("Entity", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
        
        if (entityProperty != null && entityProperty.GetValue(request) != null)
        {
            var val = entityProperty.GetValue(request);
            if (val != null)
            {
                return val;
            }
        }

        return request;
    }

    private static string BuildExpression(object target, DynamicRule rule)
    {
        var propName = rule.PropertyName;
        var op = rule.Operator.ToLowerInvariant();
        var val = rule.Value;

        var propInfo = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => string.Equals(p.Name, propName, StringComparison.OrdinalIgnoreCase));
        
        if (propInfo == null)
        {
            return "false"; // Fail if the property doesn't exist
        }

        var propType = Nullable.GetUnderlyingType(propInfo.PropertyType) ?? propInfo.PropertyType;

        if (propType.IsEnum)
        {
            return $"{propInfo.Name} == \"{val}\"";
        }

        if (propType == typeof(string))
        {
            return op switch
            {
                "contains" => $"{propInfo.Name}.Contains(\"{val}\")",
                "startswith" => $"{propInfo.Name}.StartsWith(\"{val}\")",
                "endswith" => $"{propInfo.Name}.EndsWith(\"{val}\")",
                "notequal" or "!=" or "notequals" => $"{propInfo.Name} != \"{val}\"",
                _ => $"{propInfo.Name} == \"{val}\""
            };
        }

        if (propType == typeof(bool))
        {
            return $"{propInfo.Name} == {val.ToLowerInvariant()}";
        }

        var normalizedOp = op switch
        {
            "equal" or "equals" => "==",
            "notequal" or "notequals" => "!=",
            "lessthan" => "<",
            "lessthanorequal" => "<=",
            "greaterthan" => ">",
            "greaterthanorequal" => ">=",
            _ => op
        };

        return $"{propInfo.Name} {normalizedOp} {val}";
    }
}

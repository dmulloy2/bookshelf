using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using NUnit.Framework;
using Readarr.Api.V1.Tags;
using Readarr.Http;

namespace NzbDrone.App.Test
{
    [TestFixture]
    public class ApiBindingFixture
    {
        // Before .NET 10 the versioned API attribute made MVC infer [FromBody] for complex parameters. It no longer
        // does, so a POST or PUT whose parameter says nothing binds from the query string and arrives empty: tags
        // fail validation, and adding a book throws a NullReferenceException in the validator. Every complex action
        // parameter has to say where it comes from.
        [Test]
        public void every_complex_action_parameter_should_declare_its_binding_source()
        {
            var assemblies = new[] { typeof(TagController).Assembly, typeof(VersionedApiControllerAttribute).Assembly };

            var missing = new List<string>();

            foreach (var controller in assemblies.SelectMany(a => a.GetTypes())
                                                 .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t)))
            {
                foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (action.IsSpecialName || action.DeclaringType == typeof(object) || action.DeclaringType.Assembly != controller.Assembly)
                    {
                        continue;
                    }

                    var isAction = action.GetCustomAttributes(true).Any(a => a is IActionHttpMethodProvider || a.GetType().Name.StartsWith("Rest"));

                    if (!isAction)
                    {
                        continue;
                    }

                    foreach (var parameter in action.GetParameters().Where(p => !IsSimple(p.ParameterType)))
                    {
                        if (!parameter.GetCustomAttributes(true).OfType<IBindingSourceMetadata>().Any())
                        {
                            missing.Add($"{controller.Name}.{action.Name}({parameter.ParameterType.Name} {parameter.Name})");
                        }
                    }
                }
            }

            missing.Should().BeEmpty("these parameters need [FromBody], [FromQuery] or [FromForm]");
        }

        private static bool IsSimple(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;

            return type.IsPrimitive ||
                   type.IsEnum ||
                   type == typeof(string) ||
                   type == typeof(decimal) ||
                   type == typeof(Guid) ||
                   type == typeof(DateTime) ||
                   type == typeof(DateTimeOffset) ||
                   type == typeof(TimeSpan) ||
                   type == typeof(Uri) ||
                   type == typeof(CancellationToken);
        }
    }
}

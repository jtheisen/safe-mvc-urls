using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace MonkeyBusters
{
    static class CustomerAttributeExtensions
    {
        public static T GetCustomAttribute<T>(this MemberInfo element, Boolean inherit = false)
            where T : Attribute
        {
            var attributes = element.GetCustomAttributes(typeof(T), inherit: inherit) as T[];

            return attributes.FirstOrDefault();
        }
    }
}

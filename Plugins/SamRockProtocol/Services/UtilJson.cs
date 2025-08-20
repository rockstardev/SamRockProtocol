using System;
using Newtonsoft.Json;

namespace SamRockProtocol.Services
{
    public static class UtilJson
    {
        public static T Parse<T>(string json, out Exception parsingException)
        {
            try
            {
                var model = JsonConvert.DeserializeObject<T>(json);
                parsingException = null;
                return model;
            }
            catch (Exception ex)
            {
                parsingException = ex;
                return default;
            }
        }
        
        

        private static string SerializeJson(object obj)
        {
            try
            {
                return JsonConvert.SerializeObject(obj);
            }
            catch
            {
                return "<failed-to-serialize>";
            }
        }
        
        public static string ToJson(this object obj)
        {
            return SerializeJson(obj);
        }
    }
}

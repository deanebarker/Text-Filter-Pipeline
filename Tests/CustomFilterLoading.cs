﻿using System;
using BlendInteractive.Denina.Core;
using BlendInteractive.Denina.Core.Documentation;
using BlendInteractive.Denina.Core.Filters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tests.Utility;

namespace Tests
{
    [TestClass]
    public class CustomFilterLoading
    {
        [TestMethod]
        public void LoadCustomFiltersFromType()
        {
            Pipeline.AddType(typeof (CustomFilters));
            Assert.IsTrue(Pipeline.CommandMethods.ContainsKey("custom.mymethod"));

            var pipeline = new Pipeline();
            pipeline.AddCommand("custom.MyMethod");
            Assert.AreEqual("MyMethod", pipeline.Execute());
        }

        [TestMethod]
        public void LoadCustomFiltersFromTypeWithCategoryName()
        {
            Pipeline.AddType(typeof (CustomFilters), "something");
            Assert.IsTrue(Pipeline.CommandMethods.ContainsKey("something.mymethod"));

            var pipeline = new Pipeline();
            pipeline.AddCommand("something.MyMethod");
            Assert.AreEqual("MyMethod", pipeline.Execute());
        }

        [TestMethod]
        public void OverwriteExistingFilter()
        {
            var pipeline = new Pipeline("Text.Append BAR");
            Assert.AreEqual("FOOBAR", pipeline.Execute("FOO"));

            Pipeline.AddType(typeof (OverwriteFilterTestClass));  // This should overwrite Core.Append

            Assert.AreEqual("FOOBAZ", pipeline.Execute("FOO"));

            // Now add the old filter back, or else another test fails...
            Pipeline.AddType(typeof(Core));
        }

        [TestMethod]
        public void LoadNakedMethod()
        {
            Pipeline.AddMethod(GetType().GetMethod("DoSomething"), "Deane", "DoSomething");
            
            Assert.IsTrue(Pipeline.CommandMethods.ContainsKey("deane.dosomething"));

            var pipeline = new Pipeline();
            pipeline.AddCommand("Deane.DoSomething");

            Assert.AreEqual("It worked!", pipeline.Execute());
        }

        [TestMethod]
        public void LoadMethod()
        {
            Pipeline.AddMethod(GetType().GetMethod("DoSomethingElse"), "Deane");

            Assert.IsTrue(Pipeline.CommandMethods.ContainsKey("deane.dosomethingelse"));

            var pipeline = new Pipeline();
            pipeline.AddCommand("Deane.DoSomethingElse");

            Assert.AreEqual("It worked!", pipeline.Execute());           
        }


        public static string DoSomething(string input, PipelineCommand command)
        {
            return "It worked!";
        }

        [Filter("DoSomethingElse")]
        public static string DoSomethingElse(string input, PipelineCommand command)
        {
            return "It worked!";
        }
    
    }

    [Filters("Text")]
    internal static class OverwriteFilterTestClass
    {
        [Filter("Append")]
        public static string Append(string input, PipelineCommand command)
        {
            return String.Concat(input, "BAZ");
        }
    }


}
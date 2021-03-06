﻿using DeninaSharp.Core.Configuration;
using DeninaSharp.Core.Documentation;
using DeninaSharp.Core.Utility;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace DeninaSharp.Core
{
    public partial class Pipeline
    {
        // Some magic strings
        public const string GLOBAL_VARIABLE_NAME = "__global";
        public const string WRITE_TO_VARIABLE_COMMAND = "core.writeto";
        public const string READ_FROM_VARIABLE_COMMAND = "core.readfrom";
        public const string INCLUSION_COMMAND = "core.include";
        public const string LABEL_COMMAND = "core.label";
        public const string FINAL_COMMAND_LABEL = "end";

        // Static members

        public static readonly ConcurrentDictionary<string, Type> Types = new ConcurrentDictionary<string, Type>(); // This is just to keep them handy for the documentor

        private static readonly ConcurrentDictionary<string, MethodInfo> commandMethods = new ConcurrentDictionary<string, MethodInfo>();
        private static readonly ConcurrentDictionary<string, string> hiddenCommandMethods = new ConcurrentDictionary<string, string>();

        public delegate void PipelineEventHandler(object o, PipelineEventArgs e);
        public static event PipelineEventHandler PipelineComplete;
        public static event PipelineEventHandler PipelineCreated;

        public delegate void DocumentationEventHandler(object o, DocumentationEventArgs e);
        public static event DocumentationEventHandler FilterDocLoading;
        public static event DocumentationEventHandler CategoryDocLoading;

        public delegate void CommandEventHandler(object o, CommandEventArgs e);
        public static event CommandEventHandler CommandLoading;

        private static readonly ConcurrentDictionary<string, CommandDoc> commandDocs = new ConcurrentDictionary<string, CommandDoc>();
        public static ReadOnlyDictionary<string, CommandDoc> CommandDocs => new ReadOnlyDictionary<string, CommandDoc>(commandDocs);

        private static readonly ConcurrentDictionary<string, CategoryDoc> categoryDocs = new ConcurrentDictionary<string, CategoryDoc>();
        public static ReadOnlyDictionary<string, CategoryDoc> CategoryDocs => new ReadOnlyDictionary<string, CategoryDoc>(categoryDocs);

        private static ConcurrentDictionary<string, Func<PipelineCommand, IEnumerable<PipelineCommand>>> commandFactories { get; set; }

        // Instance members

        public string NextCommandLabel { get; set; }
        private Stopwatch timer = new Stopwatch();
        private readonly List<PipelineCommand> commands = new List<PipelineCommand>();
        private Dictionary<string, PipelineVariable> variables = new Dictionary<string, PipelineVariable>();
        private static Dictionary<string, PipelineVariable> globalVariables = new Dictionary<string, PipelineVariable>();
        public List<ExecutionLog> LogEntries { get; private set; }


        public delegate void FilterEventHandler(Pipeline s, FilterEventArgs e);
        public event FilterEventHandler FilterExecuting;
        public event FilterEventHandler FilterExecuted;

        /* IMPORTANT NOTE ABOUT THESE METHODS
        Variables are resolved on first execution of a pipeline.
        This means that after that first execution, **there are no more variables**.
        So, if you execute a pipline, THEN bind this event...it won't function how you expect
        After the first execution, the engine doesn't "get" anymore variables.
        They were all "gotten" on the first execution.
        */
        public delegate void VariableEventHandler(Pipeline s, VariableEventArgs e);
        public event VariableEventHandler VariableRetrieving;
        public event VariableEventHandler VariableRetrieved;

        // This is the delegate through which we run all the filters
        // We do this through a delegate so we can handle anonymous functions
        public delegate string FilterDelegate(string input, PipelineCommand command, ExecutionLog log);

        // This loads the filters from this assembly
        public static void Init()
        {
            // Reset all the commands, and reload from this assembly
            commandMethods.Clear();
            ReflectAssembly(typeof(Pipeline).Assembly);
        }

        // This resets the pipeline to default -- varibales and events -- but does not reload.
        // This is used mainly for testing, to reset the pipeline between tests
        public static void Reset()
        {
            ClearGlobalVariables();
            PipelineComplete = null;
            PipelineCreated = null;
            FilterDocLoading = null;
            CategoryDocLoading = null;
            CommandLoading = null;
            ClearCommandFactories();
        }

        public Pipeline(string commandString = null)
        {
            // Add this assembly to initialze the filters
            // I moved this to the static constructor so it only inits once and always inits when accessed statically
            // (I'm leaving this comment here in case I get confused later...)
            // AddAssembly(Assembly.GetExecutingAssembly());

            if (!string.IsNullOrWhiteSpace(commandString))
            {
                AddCommand(commandString);
            }

            var configSection = (PipelineConfigSection) ConfigurationManager.GetSection("denina");
            if (configSection != null)
            {
                foreach (PipelineConfigVariable configVariable in configSection.Variables)
                {
                    SetGlobalVariable(configVariable.Key, configVariable.Value, true);
                }
            }

            LogEntries = new List<ExecutionLog>();

            // Raise the PipelineCreated event
            var eventArgs = new PipelineEventArgs(this, null);
            OnPipelineCreated(eventArgs);
        }

        public static ReadOnlyDictionary<string, MethodInfo> CommandMethods
        {
            get { return new ReadOnlyDictionary<string, MethodInfo>(commandMethods); }
        }

        public ReadOnlyCollection<PipelineCommand> Commands
        {
            get { return commands.AsReadOnly(); }
        }

        public ReadOnlyDictionary<string, PipelineVariable> Variables
        {
            get { return new ReadOnlyDictionary<string, PipelineVariable>(variables); }
        }

        public static ReadOnlyDictionary<string, PipelineVariable> GlobalVariables
        {
            get { return new ReadOnlyDictionary<string, PipelineVariable>(globalVariables); }
        }

        public IEnumerable<object> RegexFactoryCommands { get; private set; }

        public static void ReflectAssembly(Assembly assembly)
        {
            // Iterate all the classes in this assembly
            foreach (Type thisType in assembly.GetTypes())
            {
                // Does this assembly have the TextFilters attribute?
                if (thisType.GetCustomAttributes(typeof (FiltersAttribute), true).Any())
                {
                    // Process It
                    ReflectType(thisType);
                }
            }
        }

        public static void ReflectType(Type type, string category = null)
        {
            category = category ?? ((FiltersAttribute)type.GetCustomAttributes(typeof(FiltersAttribute), true).FirstOrDefault())?.Category ?? type.Name;
            category = StringUtilities.RemoveNonLettersAndDigits(category);

            // Add to the documentation
            categoryDocs.TryRemove(category, out CategoryDoc whoCares);

            // Process the category doc through the event
            var categoryDoc = new CategoryDoc(type);
            var categoryDocLoadedEventArgs = new DocumentationEventArgs(categoryDoc);
            OnCategoryDocLoading(categoryDocLoadedEventArgs);

            if (!categoryDocLoadedEventArgs.Cancel)
            {
                // Add the processed category doc
                categoryDocs.TryAdd(category, categoryDocLoadedEventArgs.CategoryDoc);
            }

            foreach (var method in type.GetMethods().Where(m => m.GetCustomAttributes(typeof (FilterAttribute), true).Any()))
            {
                ReflectMethod(method, category, null);
            }
        }

        public static void ReflectMethod(FilterDelegate method, string category = null, string name = null)
        {
            ReflectMethod(method.Method, category, name);
        }

        public static void ReflectMethod(MethodInfo method, string category = null, string name = null)
        {
            // This has to be a list for the sole reason that we might have more than one FilterAttribute
            var identifiers = new List<Tuple<string, string, string>>();

            if (name != null)
            {
                // The identifiers are explicit, yay!
                identifiers.Add(Tuple.Create(category ?? method.DeclaringType.Name, name, (string)null));
            }
            else
            {
                // Do we have filter attributes?
                foreach (var filterAttribute in method.GetCustomAttributes<FilterAttribute>())
                {
                    // Get stuff off the attribute
                    identifiers.Add(Tuple.Create(category ?? method.DeclaringType.Name, filterAttribute.Name ?? method.Name, filterAttribute.Description));
                }

                // If we have no filter attributes either, then reflect some identifiers from the methodd
                if (!identifiers.Any())
                {
                    identifiers.Add(Tuple.Create(method.DeclaringType.Name, method.Name, (string)null));
                }
            }

            // Add the filter under every identifier
            foreach (var identifier in identifiers)
            {
                AddFilter(
                     method,
                     identifier.Item1,
                     identifier.Item2,
                     identifier.Item3
                 );
            }
        }

        public static void AddFilter(FilterDelegate method, string category, string name, string description = null)
        {
            AddFilter(method.Method, category, name, description);
        }

        public static void AddFilter(MethodInfo method, string category, string name, string description = null)
        {
            category = StringUtilities.RemoveNonLettersAndDigits(category);
            name = StringUtilities.RemoveNonLettersAndDigits(name);

            var fullyQualifiedCommandName = string.Concat(category, ".", name);

            // Check if it has any requirements
            foreach (RequiresAttribute dependency in method.GetCustomAttributes(typeof(RequiresAttribute), true))
            {
                if (Type.GetType(dependency.TypeName) == null && !hiddenCommandMethods.ContainsKey(fullyQualifiedCommandName))
                {
                    // This dependency doesn't exist, so we're not going to load this command.
                    // We're going to add this to the hidden commands dictionary, so we can give a more specific error message if this command is requested.
                    hiddenCommandMethods.TryAdd(fullyQualifiedCommandName, string.Format(@"Command ""{0}"" could not be loaded due to a missing dependency on type ""{1}""", fullyQualifiedCommandName, dependency.TypeName));
                    return;
                }
            }

            // Process this through the event
            var commandEventArgs = new CommandEventArgs(fullyQualifiedCommandName, method);
            OnCommandLoading(commandEventArgs);
            if(commandEventArgs.Cancel)
            {
                // If they cancel, we just abandon and don't load the command, or the documentation
                return;
            }

            // Load the processed command
            commandMethods.TryRemove(commandEventArgs.FullyQualifiedCommandName.ToLower(), out MethodInfo discardCommandMethods); // Remove it if it exists already                  
            commandMethods.TryAdd(commandEventArgs.FullyQualifiedCommandName.ToLower(), commandEventArgs.Method);

            // Remove this if it exists
            commandDocs.TryRemove(fullyQualifiedCommandName, out CommandDoc discardCommandDocs);

            // Process the filter doc through the event
            var filterDoc = new CommandDoc(method, name, description);
            var filterDocLoadedEventArgs = new DocumentationEventArgs(filterDoc);
            OnFilterDocLoading(filterDocLoadedEventArgs);

            // Do they want to cancel?
            if (!filterDocLoadedEventArgs.Cancel)
            {
                // Add the processed filter doc
                commandDocs.TryAdd(fullyQualifiedCommandName, filterDocLoadedEventArgs.CommandDoc);
            }

        }

        public string Execute(string input = null)
        {
            // Clear the debug data
            LogEntries.Clear();

            // Have any commandFactories been defined?
            if (commandFactories != null)
            {
                // We need to execute any command factories and modify the command list
                // We can't do a foreach, because we're going to modify this list
                // Also: _this does not account for recursion or circular inclusions!!!!_
                // The GetLogicHashCode method can be used to compare command logic to ensure uniqueness

                for (var i = 0; i < commands.Count(); i++)
                {
                    var thisCommand = commands[i];
                    var factoryCommand = commandFactories.FirstOrDefault(c => Regex.IsMatch(thisCommand.NormalizedCommandName, StringUtilities.ConvertWildcardToRegex(c.Key), RegexOptions.IgnoreCase));

                    if (factoryCommand.Value != null) // This test is a little weird. KeyValue is a struct, so a normal null check doesn't work, so I do the null check against the Value...
                    {
                        // Run the factory, passing the entire command
                        var commandsToInclude = factoryCommand.Value(thisCommand);

                        // Include the source, for logging
                        commandsToInclude.ToList().ForEach(c =>
                        {
                            c.CommandFactorySource = thisCommand.OriginalText;
                        });

                        // Insert these commands AFTER the factory command
                        commands.InsertRange(i + 1, commandsToInclude);

                        // Delete the factory command
                        commands.Remove(thisCommand);

                        // Inserted commands will also be processed for command factories, since the next iteration of this loop will pickup at the first inserted command
                    }
                }
            }
            // At this point, all command factories should be resolved for this and subsequent executions

            // Add a pass-through command at the end just to hold a label called "end".
            if (commands.Any(c => c.Label == FINAL_COMMAND_LABEL))
            {
                commands.Remove(commands.First(c => c.Label == FINAL_COMMAND_LABEL));
            }
            commands.Add(
                new PipelineCommand()
                {
                    FullyQualifiedCommandName = LABEL_COMMAND,
                    Label = FINAL_COMMAND_LABEL,
                    CommandArgs = new Dictionary<object, string>() { { 0, FINAL_COMMAND_LABEL } }
                }
            );

            // We set the global variable to the incoming string. It will be modified and eventually returned from this variable slot.
            SetVariable(GLOBAL_VARIABLE_NAME, input);

            // We're going to set up a linked list of commands. Each command holds a reference to the next command in its SendToLabel property. The last command is NULL.
            var commandQueue = new Dictionary<string, PipelineCommand>();
            for (var index = 0; index < commands.Count; index++)
            {
                var thisCommand = commands[index];

                // If this is a "Label" command, then we need to get the label "out" to a property
                if (thisCommand.NormalizedCommandName == LABEL_COMMAND)
                {
                    thisCommand.Label = thisCommand.DefaultArgument;
                }
                
                // If (1) this command doesn't already have a SendToLabel (it...shouldn't...I don't think), and (2) we're not on the last command, then set the SendToLabel of this command to the Label of the next command
                if (thisCommand.SendToLabel == null && index < commands.Count - 1)
                {
                    thisCommand.SendToLabel = commands[index + 1].Label;
                }

                // Add this command to the queue, keyed to its Label
                commandQueue.Add(thisCommand.Label.ToLower(), thisCommand);
            }

            // We're going to stay in this loop, resetting "command" each iteration, until SendToLabel is NULL
            NextCommandLabel = commandQueue.First().Value.Label;
            while(true)
            {
                // Do we have a next command?
                if (NextCommandLabel == null)
                {
                    // Stick a fork in us, we're done
                    break;
                }
                
                // Does the specified next command exist?
                if (!commandQueue.ContainsKey(NextCommandLabel.ToLower()))
                {
                    throw new Exception(string.Format("Specified command label \"{0}\" does not exist in the command queue.", NextCommandLabel));
                }

                // Get the next command
                var command = commandQueue[NextCommandLabel.ToLower()];

                // Create the debug entry
                var executionLog = new ExecutionLog(command, Variables);

                // Are we writing to a variable?
                if (command.NormalizedCommandName == WRITE_TO_VARIABLE_COMMAND)
                {
                    // Get the active text and copy it to a different variable
                    SetVariable(command.OutputVariable, GetVariable(GLOBAL_VARIABLE_NAME));
                    NextCommandLabel = command.SendToLabel;
                    continue;
                }

                // Are we reading from a variable?
                if (command.NormalizedCommandName == READ_FROM_VARIABLE_COMMAND)
                {
                    // Get the variable and copy it into the active text
                    SetVariable(GLOBAL_VARIABLE_NAME, GetVariable(command.InputVariable));
                    NextCommandLabel = command.SendToLabel;
                    continue;
                }

                // Is this a label?
                if (command.NormalizedCommandName == LABEL_COMMAND)
                {
                    NextCommandLabel = command.SendToLabel;
                    continue;
                }
                
                // Note that the above commands will never actually execute. This is why their methods are just empty shells...

                // Do we a method for this command?
                if (!CommandMethods.ContainsKey(command.NormalizedCommandName))
                {
                    // This command doesn't exist. We're going to try to be helpful and let the user know if it's becaue of a missing dependency.
                    var errorString = hiddenCommandMethods.ContainsKey(command.NormalizedCommandName)
                        ? string.Format(hiddenCommandMethods[command.NormalizedCommandName])  // This should be the reason the command is hidden
                        : string.Format(@"No command loaded for ""{0}""", command.FullyQualifiedCommandName);

                    throw new DeninaException(errorString);
                }

                // Set a pipeline reference which can be accessed inside the filter method
                command.Pipeline = this;

                // Resolve any arguments that are actually variable names
                command.ResolveArguments();

                // Execute
                var method = CommandMethods[command.NormalizedCommandName];
                try
                {
                    timer.Reset();
                    timer.Start();

                    // Get the input from the designated variable
                    var filterInput = (string)GetVariable(command.InputVariable);

                    // Process the input through the event
                    var executingFilterEventArgs = new FilterEventArgs(command, filterInput, null);
                    OnFilterExecuting(executingFilterEventArgs);
                    filterInput = executingFilterEventArgs.Input;
                    command = executingFilterEventArgs.Command; // I'm not sure I need to do this, but just to be explicit...

                    // TO DO: How do we track changes made to the input in an event?
                    executionLog.InputValue = GetVariable(command.InputVariable).ToString();

                    // This is where we make the actual method call. We get the text out of the InputVariable slot, and we put it back into the OutputVariable slot. (These are usually the same slot...)
                    // We create a delete so that we can use anonymous functions. Since non-anonymous functions are static, and anonymous functions aren't static, we have to create a 
                    // delegate so we can handle both
                    var filter = (FilterDelegate)Delegate.CreateDelegate(typeof(FilterDelegate), null, method);
                    var filterOutput = filter(filterInput, command, executionLog);

                    // Process the output through the event
                    var executedFilterEventArgs = new FilterEventArgs(command, null, filterOutput);
                    OnFilterExecuted(executedFilterEventArgs);
                    filterOutput = executedFilterEventArgs.Output;

                    // TO DO: How do we track changes made to the output in an event?
                    executionLog.OutputValue = filterOutput.ToString();

                    // If we're appending, tack this onto what was passed in (really, prepend was was passed in)
                    if (command.AppendToOutput)
                    {
                        filterOutput = string.Concat(GetVariable(command.OutputVariable), filterOutput);
                    }

                    // We're going to "SafeSet" this, so they can't pipe output to a read-only variable
                    SafeSetVariable(command.OutputVariable, filterOutput);

                    executionLog.ElapsedTime = timer.ElapsedMilliseconds;

                    // If we got here with no exception
                    executionLog.SuccessfullyExecuted = true;
                }
                catch (DeninaException e)
                {
                    e.CurrentCommandText = command.OriginalText;
                    e.CurrentCommandName = command.NormalizedCommandName;
                    throw;
                }
                // We are not going to handle a non-DeninaException. We'll just let that bubble up to the implementation's error handler

                // Set the pointer to the next command
                NextCommandLabel = command.SendToLabel;

                LogEntries.Add(executionLog);
            }

            var finalOutput = GetVariable(GLOBAL_VARIABLE_NAME).ToString();

            // Raise the PipelineCompleted event
            var eventArgs = new PipelineEventArgs(this, finalOutput);
            OnPipelineComplete(eventArgs);
            finalOutput = eventArgs.Value;

            return finalOutput;

        }

        public bool IsSet(string key)
        {
            return variables.ContainsKey(key);
        }

        public static bool IsSetGlobally(string key)
        {
            return globalVariables.ContainsKey(key);
        }

        public object GetVariable(string key, bool checkGlobal = false)
        {
            key = PipelineCommandParser.NormalizeVariableName(key);

            // See important comment at declaration above
            var variableRetrievingEvents = new VariableEventArgs(this, key);
            OnVariableRetrieving(variableRetrievingEvents);
            key = variableRetrievingEvents.Key;

            if (!variables.ContainsKey(key))
            {
                if (checkGlobal)
                {
                    if (IsSetGlobally(key))
                    {
                        return GetGlobalVariable(key);
                    }
                }

                throw new DeninaException(string.Format("Attempt to access non-existent variable: \"{0}\"", key));
            }

            var value = variables[PipelineCommandParser.NormalizeVariableName(key)].Value;

            // See important comment at declaration above
            var variableRetrievedEvents = new VariableEventArgs(this, key, value);
            OnVariableRetrieved(variableRetrievedEvents);
            value = variableRetrievedEvents.Value;

            return value ?? string.Empty;
        }

        public static object GetGlobalVariable(string key)
        {
            key = PipelineCommandParser.NormalizeVariableName(key);

            if (!globalVariables.ContainsKey(key))
            {
                throw new DeninaException(string.Format("Attempt to access non-existent variable: \"{0}\"", key));
            }

            return globalVariables[PipelineCommandParser.NormalizeVariableName(key)].Value;           
        }

        public void SetVariable(string key, object value, bool readOnly = false)
        {
            key = PipelineCommandParser.NormalizeVariableName(key);
            variables.Remove(key);
            variables.Add(
                key,
                new PipelineVariable(
                    key,
                    value,
                    readOnly
                    )
            );
        }

        public static void UnsetGlobalVariable(string key)
        {
            globalVariables.Remove(key);
        }

        public static void SetGlobalVariable(string key, object value, bool readOnly = false)
        {
            key = PipelineCommandParser.NormalizeVariableName(key);
            globalVariables.Remove(key);
            globalVariables.Add(
                key,
                new PipelineVariable(
                    key,
                    value,
                    readOnly
                    )
            );
        }

        public static void ClearGlobalVariables()
        {
            globalVariables.Clear();
        }

        // This will refuse to set variables flagged as read-only
        public void SafeSetVariable(string key, object value, bool readOnly = false)
        {
            // Do we have a variable with the same name that's readonly?
            if (variables.Any(v => v.Value.Name == key && v.Value.ReadOnly))
            {
                throw new DeninaException(string.Format("Attempt to reset value of read-only variable \"{0}\"", key));
            }

            SetVariable(key, value, readOnly);
        }

        public void AddCommand(PipelineCommand command)
        {
            commands.Add(command);
        }

        public void AddCommand(string commandString)
        {
            commands.AddRange(PipelineCommandParser.ParseCommandString(commandString));
        }

        public void AddCommand(string commandName, Dictionary<object, string> commandArgs)
        {
            var command = new PipelineCommand
            {
                FullyQualifiedCommandName = commandName,
                CommandArgs = commandArgs
            };
            commands.Add(command);
        }

        public static void RemoveCommand(string commandName, string reason = null)
        {
            reason = reason ?? "It was removed from the command set";

            if (!commandMethods.ContainsKey(commandName.ToLower()))
            {
                return;
            }
            commandMethods.TryRemove(commandName.ToLower(), out MethodInfo discardCommandMethods);

            // Add this to the hidden command methods
            if (hiddenCommandMethods.ContainsKey(commandName.ToLower()))
            {
                hiddenCommandMethods.TryRemove(commandName.ToLower(), out string discardHiddenCommandMethods);
            }
            hiddenCommandMethods.TryAdd(commandName.ToLower(), string.Format(@"""{0}"" is unavailable for this reason: {1}", commandName, reason));
        }

        public static void RemoveCommandCategory(string commandCategoryName, string reason = null)
        {
            commandMethods
                .Where(x => x.Key.StartsWith(string.Concat(commandCategoryName.ToLower(), ".")))
                .Select(y => y.Key)
                .ToList()
                .ForEach(z => RemoveCommand(z, reason));
        }

        public static void RegisterCommandFactory(string key, Func<PipelineCommand, IEnumerable<PipelineCommand>> function)
        {
            if(commandFactories == null)
            {
                commandFactories = new ConcurrentDictionary<string, Func<PipelineCommand, IEnumerable<PipelineCommand>>>();
            }
            
            if(commandFactories.ContainsKey(key))
            {
                commandFactories.TryRemove(key, out Func<PipelineCommand, IEnumerable<PipelineCommand>> discardCommandFactories);
            }
            commandFactories.TryAdd(key, function);
        }

        public static void ClearCommandFactories()
        {
            commandFactories = new ConcurrentDictionary<string, Func<PipelineCommand, IEnumerable<PipelineCommand>>>();
        }

        private static void OnPipelineComplete(PipelineEventArgs e) => PipelineComplete?.Invoke(null, e);
        private static void OnPipelineCreated(PipelineEventArgs e) => PipelineCreated?.Invoke(null, e);
        private static void OnFilterDocLoading(DocumentationEventArgs e) => FilterDocLoading?.Invoke(null, e);
        private static void OnCategoryDocLoading(DocumentationEventArgs e) => CategoryDocLoading?.Invoke(null, e);
        private static void OnCommandLoading(CommandEventArgs e) => CommandLoading?.Invoke(null, e);
        private void OnFilterExecuting(FilterEventArgs e) => FilterExecuting?.Invoke(this, e);
        private void OnFilterExecuted(FilterEventArgs e) => FilterExecuted?.Invoke(this, e);
        private void OnVariableRetrieving(VariableEventArgs e) => VariableRetrieving?.Invoke(this, e);
        private void OnVariableRetrieved(VariableEventArgs e) => VariableRetrieved?.Invoke(this, e);
    }
}
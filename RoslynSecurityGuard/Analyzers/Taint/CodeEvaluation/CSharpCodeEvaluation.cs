﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynSecurityGuard.Analyzers.Locale;
using RoslynSecurityGuard.Analyzers.Utils;
using System;
using System.Collections.Generic;

namespace RoslynSecurityGuard.Analyzers.Taint
{
    /// <summary>
    /// Symbolic execution of C# code
    /// </summary>
    public class CSharpCodeEvaluation : BaseCodeEvaluation
    {
        public static List<CSharpTaintAnalyzerExtension> extensions { get; set; } = new List<CSharpTaintAnalyzerExtension>();
        

        public void VisitMethods(SyntaxNodeAnalysisContext ctx)
        {
            var node = ctx.Node as MethodDeclarationSyntax;
            try
            {
                if (node != null)
                {
                    var state = new ExecutionState(ctx);

                    foreach (var ext in extensions)
                    {
                        ext.VisitBeginMethodDeclaration(node, state);
                    }

                    VisitMethodDeclaration(node, state);

                    foreach (var ext in extensions)
                    {
                        ext.VisitEndMethodDeclaration(node, state);
                    }
                }
            }
            catch (Exception e)
            {
                //Intercept the exception for logging. Otherwise, the analyzer will failed silently.
                string methodName = node.Identifier.Text;
                string errorMsg = string.Format("Unhandle exception while visiting method {0} : {1}", methodName, e.Message);
                SGLogging.Log(errorMsg);
                SGLogging.Log(e.StackTrace, false);
                throw new Exception(errorMsg, e);
            }
        }

        /// <summary>
        /// Entry point that visit the method statements.
        /// </summary>
        /// <param name="node"></param>
        /// <param name="state"></param>
        /// <returns></returns>
        private VariableState VisitMethodDeclaration(MethodDeclarationSyntax node, ExecutionState state)
        {
            foreach (ParameterSyntax statement in node.ParameterList.Parameters)
            {
                state.AddNewValue(ResolveIdentifier(statement.Identifier), new VariableState(VariableTaint.TAINTED));
            }

            if (node.Body != null)
            {
                foreach (StatementSyntax statement in node.Body.Statements)
                {
                    VisitNode(statement, state);

                    foreach (var ext in extensions)
                    {
                        ext.VisitStatement(statement, state);
                    }
                }
            }

            //The state return is irrelevant because it is not use.
            return new VariableState(VariableTaint.UNKNOWN);
        }

        /// <summary>
        /// Statement are all segment separate by semi-colon.
        /// </summary>
        /// <param name="node"></param>
        /// <param name="ctx"></param>
        /// <param name="state"></param>
        private VariableState VisitNode(SyntaxNode node, ExecutionState state)
        {
            //SGLogging.Log(node.GetType().ToString());

            //Variable allocation
            if (node is LocalDeclarationStatementSyntax)
            {
                var declaration = (LocalDeclarationStatementSyntax)node;
                return VisitLocalDeclaration(declaration, state);
            }
            else if (node is VariableDeclarationSyntax)
            {
                var declaration = (VariableDeclarationSyntax)node;
                return VisitVariableDeclaration(declaration, state);
            }

            //Expression
            else if (node is ExpressionStatementSyntax)
            {
                var expression = (ExpressionStatementSyntax)node;
                return VisitExpressionStatement(expression, state);
            }
            else if (node is ExpressionSyntax)
            {
                var expression = (ExpressionSyntax)node;
                return VisitExpression(expression, state);
            }
            else if (node is MethodDeclarationSyntax)
            {
                var methodDeclaration = (MethodDeclarationSyntax)node;
                return VisitMethodDeclaration(methodDeclaration, state);
            }

            else
            {
                foreach (var n in node.ChildNodes())
                {
                    VisitNode(n, state);
                }
            }

            var isBlockStatement = node is BlockSyntax || node is IfStatementSyntax || node is ForEachStatementSyntax || node is ForStatementSyntax;

            if (!isBlockStatement)
            {
                SGLogging.Log("Unsupported statement " + node.GetType() + " (" + node.ToString() + ")");
            }

            return new VariableState(VariableTaint.UNKNOWN);
        }

        /// <summary>
        /// Unwrap
        /// </summary>
        /// <param name="declaration"></param>
        /// <param name="state"></param>
        /// <returns></returns>
        private VariableState VisitLocalDeclaration(LocalDeclarationStatementSyntax declaration, ExecutionState state)
        {
            return VisitVariableDeclaration(declaration.Declaration, state);
        }

        /// <summary>
        /// Evaluate expression that contains a list of assignment.
        /// </summary>
        /// <param name="declaration"></param>
        /// <param name="ctx"></param>
        /// <param name="state"></param>
        private VariableState VisitVariableDeclaration(VariableDeclarationSyntax declaration, ExecutionState state)
        {
            var variables = declaration.Variables;

            VariableState lastState = new VariableState(VariableTaint.UNKNOWN);

            foreach (var variable in declaration.Variables)
            {
                var identifier = variable.Identifier;
                var initializer = variable.Initializer;
                if (initializer is EqualsValueClauseSyntax)
                {
                    EqualsValueClauseSyntax equalsClause = initializer;

                    VariableState varState = VisitExpression(equalsClause.Value, state);
                    state.AddNewValue(ResolveIdentifier(identifier), varState);
                    lastState = varState;
                }
            }

            //
            return lastState;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="syntaxToken"></param>
        /// <returns></returns>
        private string ResolveIdentifier(SyntaxToken syntaxToken)
        {
            return syntaxToken.Text;
        }


        private VariableState VisitExpression(ExpressionSyntax expression, ExecutionState state)
        {
            //Invocation
            if (expression is InvocationExpressionSyntax)
            {
                var invocation = (InvocationExpressionSyntax)expression;
                return VisitMethodInvocation(invocation, state);
            }
            else if (expression is ObjectCreationExpressionSyntax)
            {
                var objCreation = (ObjectCreationExpressionSyntax)expression;
                return VisitObjectCreation(objCreation, state);
            }

            else if (expression is LiteralExpressionSyntax)
            {
                return new VariableState(VariableTaint.CONSTANT);
            }
            else if (expression is IdentifierNameSyntax)
            {
                var identifierName = (IdentifierNameSyntax)expression;
                return VisitIdentifierName(identifierName, state);
            }

            //Arithmetic : Addition
            else if (expression is BinaryExpressionSyntax)
            {
                var binaryExpression = (BinaryExpressionSyntax)expression;
                return VisitBinaryExpression(binaryExpression, state);
            }

            else if (expression is AssignmentExpressionSyntax)
            {
                var assignment = (AssignmentExpressionSyntax)expression;
                return VisitAssignment(assignment, state);
            }
            else if (expression is MemberAccessExpressionSyntax)
            {
                var memberAccess = (MemberAccessExpressionSyntax)expression;
                var leftExpression = memberAccess.Expression;
                var name = memberAccess.Name;
                return VisitExpression(leftExpression, state);
            }
            else if (expression is ElementAccessExpressionSyntax)
            {
                var elementAccess = (ElementAccessExpressionSyntax)expression;
                return VisitElementAccess(elementAccess, elementAccess.ArgumentList, state);
            }
            else if (expression is ArrayCreationExpressionSyntax)
            {
                var arrayCreation = (ArrayCreationExpressionSyntax)expression;
                return VisitArrayCreation(arrayCreation, state);
            }
            else if (expression is TypeOfExpressionSyntax)
            {
                var typeofEx = (TypeOfExpressionSyntax)expression;
                return new VariableState(VariableTaint.SAFE);
            }
            else if (expression is ConditionalExpressionSyntax)
            {
                var conditional = (ConditionalExpressionSyntax)expression;
                VisitExpression(conditional.Condition, state);
                var finalState = new VariableState(VariableTaint.SAFE);

                var whenTrueState = VisitExpression(conditional.WhenTrue, state);
                finalState.merge(whenTrueState);
                var whenFalseState = VisitExpression(conditional.WhenFalse, state);
                finalState.merge(whenFalseState);

                return finalState;
            }
            else if (expression is CheckedExpressionSyntax)
            {
                var checkedEx = (CheckedExpressionSyntax)expression;
                return VisitExpression(checkedEx.Expression, state);
            }
            else if (expression is QueryExpressionSyntax)
            {
                var query = (QueryExpressionSyntax)expression;
                var body = query.Body;
                return new VariableState(VariableTaint.UNKNOWN);
            }
            else if (expression is InterpolatedStringExpressionSyntax)
            {
                var interpolatedString = (InterpolatedStringExpressionSyntax)expression;

                return VisitInterpolatedString(interpolatedString, state);
            }

            SGLogging.Log("Unsupported expression " + expression.GetType() + " (" + expression.ToString() + ")");

            //Unsupported expression
            return new VariableState(VariableTaint.UNKNOWN);
        }

        private VariableState VisitInterpolatedString(InterpolatedStringExpressionSyntax interpolatedString, ExecutionState state)
        {

            var varState = new VariableState(VariableTaint.CONSTANT);

            foreach (var content in interpolatedString.Contents)
            {
                var textString = content as InterpolatedStringTextSyntax;
                if (textString != null)
                {
                    varState = varState.merge(new VariableState(VariableTaint.CONSTANT));
                }
                var interpolation = content as InterpolationSyntax;
                if (interpolation != null)
                {
                    var expressionState = VisitExpression(interpolation.Expression, state);
                    varState = varState.merge(expressionState);
                }
            }
            return varState;
        }

        private VariableState VisitElementAccess(ElementAccessExpressionSyntax elementAccess, BracketedArgumentListSyntax argumentList, ExecutionState state)
        {
            foreach (var argument in argumentList.Arguments)
            {
                VisitExpression(argument.Expression, state);
            }
            return new VariableState(VariableTaint.UNKNOWN);
        }

        private VariableState VisitExpressionStatement(ExpressionStatementSyntax node, ExecutionState state)
        {
            return VisitExpression(node.Expression, state); //Simply unwrap the expression
        }

        private VariableState VisitMethodInvocation(InvocationExpressionSyntax node, ExecutionState state)
        {
            return VisitInvocationAndCreation(node, node.ArgumentList, state);
        }

        private VariableState VisitObjectCreation(ObjectCreationExpressionSyntax node, ExecutionState state)
        {
            return VisitInvocationAndCreation(node, node.ArgumentList, state);
        }

        private VariableState VisitArrayCreation(ArrayCreationExpressionSyntax node, ExecutionState state)
        {
            var arrayInit = node.Initializer;

            var finalState = new VariableState(VariableTaint.SAFE);
            if (arrayInit != null)
            {
                foreach (var ex in arrayInit.Expressions)
                {
                    var exprState = VisitExpression(ex, state);
                    finalState = finalState.merge(exprState);
                }
            }
            return finalState;
        }

        /// <summary>
        /// Logic for each method invocation (including constructor)
        /// The argument list is required because <code>InvocationExpressionSyntax</code> and 
        /// <code>ObjectCreationExpressionSyntax</code> do not share a common interface.
        /// </summary>
        /// <param name="node"></param>
        /// <param name="argList"></param>
        /// <param name="state"></param>
        /// <returns></returns>
        private VariableState VisitInvocationAndCreation(ExpressionSyntax node, ArgumentListSyntax argList, ExecutionState state)
        {

            var symbol = state.GetSymbol(node);
            MethodBehavior behavior = behaviorRepo.GetMethodBehavior(symbol);

            int i = 0;
            if (argList == null)
            {
                return new VariableState(VariableTaint.UNKNOWN);
            }

            var returnState = new VariableState(VariableTaint.SAFE);

            foreach (var argument in argList.Arguments)
            {

                var argumentState = VisitExpression(argument.Expression, state);

                if (symbol != null)
                {
                    SGLogging.Log(symbol.ContainingType + "." + symbol.Name + " -> " + argumentState);
                }

                if (behavior != null)
                { //If the API is at risk
                    if ((argumentState.taint == VariableTaint.TAINTED || //Tainted values
                        argumentState.taint == VariableTaint.UNKNOWN) &&
                        Array.Exists(behavior.injectablesArguments, element => element == i) //If the current parameter can be injected.
                        )
                    {
                        var newRule = LocaleUtil.GetDescriptor(behavior.localeInjection);
                        var diagnostic = Diagnostic.Create(newRule, node.GetLocation());
                        state.AnalysisContext.ReportDiagnostic(diagnostic);
                    }
                    else if (argumentState.taint == VariableTaint.CONSTANT && //Hard coded value
                        Array.Exists(behavior.passwordArguments, element => element == i) //If the current parameter is a password
                        )
                    {

                        var newRule = LocaleUtil.GetDescriptor(behavior.localePassword);
                        var diagnostic = Diagnostic.Create(newRule, node.GetLocation());
                        state.AnalysisContext.ReportDiagnostic(diagnostic);
                    }

                    else if ( //
                        Array.Exists(behavior.taintFromArguments, element => element == i))
                    {
                        returnState = returnState.merge(argumentState);
                    }
                }

                //TODO: tainted all object passed in argument

                i++;
            }

            //Additionnal analysis by extension
            foreach (var ext in extensions)
            {
                ext.VisitInvocationAndCreation(node, argList, state);
            }

            var hasTaintFromArguments = behavior?.taintFromArguments?.Length > 0;
            if (hasTaintFromArguments)
            {
                return returnState;
            }
            else
            {
                return new VariableState(VariableTaint.UNKNOWN);
            }

        }

        private VariableState VisitAssignment(AssignmentExpressionSyntax node, ExecutionState state)
        {

            var symbol = state.GetSymbol(node.Left);
            MethodBehavior behavior = behaviorRepo.GetMethodBehavior(symbol);

            var variableState = VisitExpression(node.Right, state);

            if (node.Left is IdentifierNameSyntax)
            {
                var assignmentIdentifier = node.Left as IdentifierNameSyntax;
                state.MergeValue(ResolveIdentifier(assignmentIdentifier.Identifier), variableState);
            }

            if (behavior != null && //Injection
                    behavior.isInjectableField &&
                    variableState.taint != VariableTaint.CONSTANT && //Skip safe values
                    variableState.taint != VariableTaint.SAFE)
            {
                var newRule = LocaleUtil.GetDescriptor(behavior.localeInjection);
                var diagnostic = Diagnostic.Create(newRule, node.GetLocation());
                state.AnalysisContext.ReportDiagnostic(diagnostic);
            }
            if (behavior != null && //Known Password API
                    behavior.isPasswordField &&
                    variableState.taint == VariableTaint.CONSTANT //Only constant
                    )
            {
                var newRule = LocaleUtil.GetDescriptor(behavior.localePassword);
                var diagnostic = Diagnostic.Create(newRule, node.GetLocation());
                state.AnalysisContext.ReportDiagnostic(diagnostic);
            }


            //TODO: tainted the variable being assign.

            //Additionnal analysis by extension
            foreach (var ext in extensions)
            {
                ext.VisitAssignment(node, state, behavior, symbol, variableState);
            }

            return variableState;
        }


        /// <summary>
        /// Identifier name include variable name.
        /// </summary>
        /// <param name="expression"></param>
        /// <param name="state"></param>
        /// <returns></returns>
        private VariableState VisitIdentifierName(IdentifierNameSyntax expression, ExecutionState state)
        {
            var value = ResolveIdentifier(expression.Identifier);
            return state.GetValueByIdentifier(value);
        }

        /// <summary>
        /// Combine the state of the two operands. Binary expression include concatenation.
        /// </summary>
        /// <param name="expression"></param>
        /// <param name="state"></param>
        /// <returns></returns>
        private VariableState VisitBinaryExpression(BinaryExpressionSyntax expression, ExecutionState state)
        {
            VariableState left = VisitExpression(expression.Left, state);
            VariableState right = VisitExpression(expression.Right, state);
            return left.merge(right);
        }

    }
}

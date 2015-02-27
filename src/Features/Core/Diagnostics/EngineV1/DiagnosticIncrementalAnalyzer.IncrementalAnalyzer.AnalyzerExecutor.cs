﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.ErrorReporting;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Diagnostics.EngineV1
{
    internal partial class DiagnosticIncrementalAnalyzer
    {
        /// <summary>
        /// AnalyzerExecutor returns analyzed data without side effect. 
        /// it might uses cache to skip unnecessary duplicate analysis, but it will never update any internal state such as state
        /// 
        /// this is not finished form. as refactoring going on, this class will become more stateless in respect to caller, and less dependency on owner.
        /// </summary>
        private class AnalyzerExecutor
        {
            private readonly DiagnosticIncrementalAnalyzer _owner;

            public AnalyzerExecutor(DiagnosticIncrementalAnalyzer owner)
            {
                _owner = owner;
            }

            public async Task<AnalysisData> GetSyntaxAnalysisDataAsync(DiagnosticAnalyzerDriver analyzerDriver, StateSet stateSet, VersionArgument versions)
            {
                try
                {
                    var document = analyzerDriver.Document;
                    var cancellationToken = analyzerDriver.CancellationToken;

                    var state = stateSet.GetState(StateType.Syntax);
                    var existingData = await state.TryGetExistingDataAsync(document, cancellationToken).ConfigureAwait(false);

                    if (CheckSyntaxVersions(document, existingData, versions))
                    {
                        return existingData;
                    }

                    var diagnosticData = await GetSyntaxDiagnosticsAsync(analyzerDriver, stateSet.Analyzer).ConfigureAwait(false);
                    return new AnalysisData(versions.TextVersion, versions.DataVersion, GetExistingItems(existingData), diagnosticData.AsImmutableOrEmpty());
                }
                catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
                {
                    throw ExceptionUtilities.Unreachable;
                }
            }

            public async Task<AnalysisData> GetDocumentAnalysisDataAsync(DiagnosticAnalyzerDriver analyzerDrvier, StateSet stateSet, VersionArgument versions)
            {
                try
                {
                    var document = analyzerDrvier.Document;
                    var cancellationToken = analyzerDrvier.CancellationToken;

                    var state = stateSet.GetState(StateType.Document);
                    var existingData = await state.TryGetExistingDataAsync(document, cancellationToken).ConfigureAwait(false);

                    if (CheckSemanticVersions(document, existingData, versions))
                    {
                        return existingData;
                    }

                    var diagnosticData = await GetSemanticDiagnosticsAsync(analyzerDrvier, stateSet.Analyzer).ConfigureAwait(false);
                    return new AnalysisData(versions.TextVersion, versions.DataVersion, GetExistingItems(existingData), diagnosticData.AsImmutableOrEmpty());
                }
                catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
                {
                    throw ExceptionUtilities.Unreachable;
                }
            }

            public async Task<AnalysisData> GetDocumentBodyAnalysisDataAsync(
                StateSet stateSet, VersionArgument versions, DiagnosticAnalyzerDriver analyzerDriver,
                SyntaxNode root, SyntaxNode member, int memberId, bool supportsSemanticInSpan, MemberRangeMap.MemberRanges ranges)
            {
                try
                {
                    var document = analyzerDriver.Document;
                    var cancellationToken = analyzerDriver.CancellationToken;

                    var state = stateSet.GetState(StateType.Document);
                    var existingData = await state.TryGetExistingDataAsync(document, cancellationToken).ConfigureAwait(false);

                    ImmutableArray<DiagnosticData> diagnosticData;
                    if (supportsSemanticInSpan && CanUseDocumentState(existingData, ranges.TextVersion, versions.DataVersion))
                    {
                        var memberDxData = await GetSemanticDiagnosticsAsync(analyzerDriver, stateSet.Analyzer).ConfigureAwait(false);

                        diagnosticData = _owner.UpdateDocumentDiagnostics(existingData, ranges.Ranges, memberDxData.AsImmutableOrEmpty(), root.SyntaxTree, member, memberId);
                        ValidateMemberDiagnostics(stateSet.Analyzer, document, root, diagnosticData);
                    }
                    else
                    {
                        // if we can't re-use existing document state, only option we have is updating whole document state here.
                        var dx = await GetSemanticDiagnosticsAsync(analyzerDriver, stateSet.Analyzer).ConfigureAwait(false);
                        diagnosticData = dx.AsImmutableOrEmpty();
                    }

                    return new AnalysisData(versions.TextVersion, versions.DataVersion, GetExistingItems(existingData), diagnosticData);
                }
                catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
                {
                    throw ExceptionUtilities.Unreachable;
                }
            }

            public async Task<AnalysisData> GetProjectAnalysisDataAsync(DiagnosticAnalyzerDriver analyzerDriver, StateSet stateSet, VersionArgument versions)
            {
                try
                {
                    var project = analyzerDriver.Project;
                    var cancellationToken = analyzerDriver.CancellationToken;

                    var state = stateSet.GetState(StateType.Project);
                    var existingData = await state.TryGetExistingDataAsync(project, cancellationToken).ConfigureAwait(false);

                    if (CheckSemanticVersions(project, existingData, versions))
                    {
                        return existingData;
                    }

                    // TODO: remove ForceAnalyzeAllDocuments at some point
                    var diagnosticData = await GetProjectDiagnosticsAsync(analyzerDriver, stateSet.Analyzer, _owner.ForceAnalyzeAllDocuments).ConfigureAwait(false);
                    return new AnalysisData(VersionStamp.Default, versions.DataVersion, GetExistingItems(existingData), diagnosticData.AsImmutableOrEmpty());
                }
                catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
                {
                    throw ExceptionUtilities.Unreachable;
                }
            }

            private bool CanUseDocumentState(AnalysisData existingData, VersionStamp textVersion, VersionStamp dataVersion)
            {
                if (existingData == null)
                {
                    return false;
                }

                // make sure data stored in the cache is one from its previous text update
                return existingData.DataVersion.Equals(dataVersion) && existingData.TextVersion.Equals(textVersion);
            }

            private static ImmutableArray<DiagnosticData> GetExistingItems(AnalysisData existingData)
            {
                if (existingData == null)
                {
                    return ImmutableArray<DiagnosticData>.Empty;
                }

                return existingData.Items;
            }

            [Conditional("DEBUG")]
            private void ValidateMemberDiagnostics(DiagnosticAnalyzer analyzer, Document document, SyntaxNode root, ImmutableArray<DiagnosticData> diagnostics)
            {
#if RANGE
                var documentBasedDriver = new DiagnosticAnalyzerDriver(document, root.FullSpan, root, CancellationToken.None);
                var expected = GetSemanticDiagnosticsAsync(documentBasedDriver, analyzer).WaitAndGetResult(documentBasedDriver.CancellationToken) ?? SpecializedCollections.EmptyEnumerable<DiagnosticData>();
                Contract.Requires(diagnostics.SetEquals(expected));
#endif
            }
        }
    }
}

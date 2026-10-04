export interface WorkspaceContextItem {
  title: string;
  value: string | number | boolean;
}

export interface WorkspaceContext {
  /**
   * Root business identity that remains visible while navigating between MFEs.
   * In the current Customer Onboarding flow this is the selected Customer.
   */
  persistentContext: WorkspaceContextItem[];

  /**
   * Information that is actively relevant to the MFE/page currently displayed.
   */
  currentContext: WorkspaceContextItem[];

  /**
   * Previously current information that the current business scope still permits
   * an MFE to reuse. Retained context is not rendered by the Shell and is not
   * authoritative business data; an MFE may promote relevant entries back to
   * currentContext or discard them.
   */
  retainedContext: WorkspaceContextItem[];
}

export const EMPTY_WORKSPACE_CONTEXT: WorkspaceContext = {
  persistentContext: [],
  currentContext: [],
  retainedContext: [],
};
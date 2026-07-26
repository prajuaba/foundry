import type { Node } from '@xyflow/react';

export interface Property {
  name: string;
  type: string;
  isKey: boolean;
  isEnum: boolean;
  attributes: string[];
}

export interface Index {
  fields: string[];
  unique: boolean;
}

export interface CachingConfig {
  enabled: boolean;
  ttlSeconds: number;
}

export interface Entity {
  name: string;
  baseClass?: string;
  softDelete: boolean;
  auditable: boolean;
  indexes: Index[];
  properties: Property[];
  
  // API Settings
  apiEnabledMethods?: string[];
  apiRoles?: Record<string, string[]>;
  apiCaching?: Record<string, CachingConfig>;
  apiBusinessRules?: Record<string, string[]>;

  // Real-Time Settings
  realTime?: boolean;
  realTimeRoles?: string[];

  // Kafka Event Streaming Outbox
  kafkaOutboxEnabled?: boolean;
  kafkaTopic?: string;

  // FileIO Storage Pipelines
  fileIoEnabled?: boolean;
  fileIoAllowedExtensions?: string[];

  // GraphQL Gateway
  graphQlEnabled?: boolean;
}

export interface Enum {
  name: string;
  values: string[];
}

export interface Connector {
  name: string;
  type: 'REST' | 'SOAP' | 'GraphQL';
  baseUrl: string;
  authType: 'None' | 'Basic' | 'ApiKey' | 'Bearer' | 'OAuth2';
  username?: string;
  password?: string;
  apiKey?: string;
  apiKeyHeaderName?: string;
  token?: string;
  soapAction?: string;
  timeoutSeconds?: number;
  maxRetries?: number;
}

export interface Assignment {
  entityProperty: string;
  sourceType: string;
  sourceValue: string;
}

export interface CustomEndpoint {
  route: string;
  method: string;
  requestType: string;
  roles: string[];
  
  // Visual Data Operation Config
  operationType: string;
  targetEntity?: string;
  filterField?: string;
  filterOperator?: string;
  filterSourceValue?: string;
  assignments: Assignment[];
  businessRules?: string[];
}

export interface DtoProperty {
  name: string;
  type: string;
  sourceEntity?: string;
  sourceProperty?: string;
  isRequired: boolean;
  attributes: string[];
}

export interface DtoModel {
  name: string;
  properties: DtoProperty[];
  kafkaOutboxEnabled?: boolean;
  kafkaTopic?: string;
  fileIoEnabled?: boolean;
  fileIoAllowedExtensions?: string[];
}

export interface WorkflowState {
  name: string;
  isInitial: boolean;
  isFinal: boolean;
  allowedRoles: string[];
  x?: number;
  y?: number;
}

export interface PropertyComparisonCondition {
  type: 'PropertyComparison';
  property: string;
  operator: 'LessThanOrEqual' | 'GreaterThanOrEqual' | 'Equal' | 'NotEqual' | 'LessThan' | 'GreaterThan';
  value: string;
}

export type WorkflowCondition = PropertyComparisonCondition;

export interface InternalApiAction {
  type: 'InternalApi';
  requestType: string;
  payloadTemplate: string;
}

export interface ExternalApiAction {
  type: 'ExternalApi';
  method: string;
  url: string;
  headers: Record<string, string>;
  bodyTemplate: string;
}

export type WorkflowAction = InternalApiAction | ExternalApiAction;

export interface WorkflowTransition {
  id: string;
  name: string;
  fromState: string;
  toState: string;
  trigger: string;
  useCustomCommand: boolean;
  requiredRoles: string[];
  conditions: WorkflowCondition[];
  actions: WorkflowAction[];
}

export interface WorkflowChoiceBranch {
  toState: string;
  conditions: WorkflowCondition[];
}

export interface WorkflowChoiceNode {
  id: string;
  name: string;
  branches: WorkflowChoiceBranch[];
  defaultState: string;
  x?: number;
  y?: number;
}

export interface WorkflowDefinition {
  id: string;
  name: string;
  entity: string;
  version: string;
  effectiveDate: string;
  expirationDate: string;
  isActive: boolean;
  states: WorkflowState[];
  transitions: WorkflowTransition[];
  choiceNodes: WorkflowChoiceNode[];
}

export type ClassNode = Node<{ entity: Entity }, 'classNode'>;
export type EnumNode = Node<{ enum: Enum }, 'enumNode'>;
export type AppNode = ClassNode | EnumNode;
